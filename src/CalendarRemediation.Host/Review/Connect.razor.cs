using System.Globalization;
using CalendarRemediation.Host.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace CalendarRemediation.Host.Review;

public partial class Connect : IAsyncDisposable
{
    [Inject] private LocalConnectionService Connection { get; set; } = null!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = null!;
    [Inject] private ILogger<Connect> Logger { get; set; } = null!;
    [Inject] private IJSRuntime JavaScript { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _polling;
    private ConnectionState? _state;
    private string? _refreshError;
    private string _province = "";
    private string _community = "";
    private string _timeZoneId = "";
    private string _previewMode = "";
    private bool _disposed;
    private ElementReference _completionForm;
    private IJSObjectReference? _module;
    private Task? _completionTask;
    private readonly HashSet<string> _completionAttemptVersions = new(StringComparer.Ordinal);
    private bool _completionFailed;

    private IEnumerable<string> AvailableZones => LocalConnectionService.LocationZones.TryGetValue(_province, out var zones)
        ? zones : [];

    private string StatusText => _refreshError is not null ? "Connection status could not be verified. Live data has been cleared."
        : _state?.Phase switch
        {
            ConnectionPhase.Disabled => "Sign-in is unavailable with the current configuration. See Developer configuration for setup details and the reported problem.",
            ConnectionPhase.SignedOut => "Not connected. Sign in below to get started.",
            ConnectionPhase.SigningIn => "Sign-in is in progress with Microsoft. This tab will return to the connection page when sign-in is complete.",
            ConnectionPhase.AwaitingCompletion => "Finishing sign-in…",
            ConnectionPhase.Connected => "Connected to a verified mailbox with read-only access.",
            ConnectionPhase.Discovering => "Connected. Calendar discovery is running.",
            ConnectionPhase.Failed => "The operation failed. Review the error and retry below.",
            _ => "Checking local connection status…"
        };

    protected override Task OnInitializedAsync() => RefreshAsync(_lifetime.Token);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Start only on the interactive instance, never during server prerendering.
        if (firstRender && !_disposed) { _polling = PollAsync(_lifetime.Token); }
        if (!_disposed && _completionTask is not { IsCompleted: false }
            && _state is { Phase: ConnectionPhase.AwaitingCompletion, Version: { Length: > 0 } version }
            && _completionAttemptVersions.Add(version))
        {
            _completionFailed = false;
            _completionTask = CompleteSignInAsync(version);
            await _completionTask;
        }
    }

    private async Task CompleteSignInAsync(string version)
    {
        try
        {
            _module ??= await JavaScript.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, "./Review/Connect.razor.js");
            if (_disposed || _state?.Phase != ConnectionPhase.AwaitingCompletion || _state.Version != version) { return; }
            await _module.InvokeVoidAsync("submitCompletion", _lifetime.Token, _completionForm);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is JSException or OperationCanceledException)
        {
            if (!_disposed && _state?.Phase == ConnectionPhase.AwaitingCompletion && _state.Version == version)
            {
                _completionFailed = true;
                StateHasChanged();
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pending = _state?.Phase is ConnectionPhase.SigningIn or ConnectionPhase.Discovering;
                await Task.Delay(TimeSpan.FromSeconds(pending ? 2 : 15), cancellationToken);
                await InvokeAsync(async () =>
                {
                    if (_disposed) { return; }
                    await RefreshAsync(cancellationToken);
                    if (!_disposed) { StateHasChanged(); }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var authentication = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var next = await Connection.GetStateAsync(authentication.User, cancellationToken);
            if (_disposed) { return; }
            if (_state?.Version != next.Version || _state?.Location != next.Location)
            {
                _province = next.Location?.Province ?? "";
                _community = next.Location?.Community ?? "";
                _timeZoneId = next.Location?.TimeZoneId ?? "";
                _previewMode = "";
            }
            // Replace the entire snapshot, including when an old circuit receives an empty version.
            _state = next;
            _refreshError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogRefreshFailure(Logger, ex);
            _state = null;
            _province = _community = _timeZoneId = "";
            _previewMode = "";
            _refreshError = "Unable to check the current session. Reload this page before taking further action.";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to refresh local connection status")]
    private static partial void LogRefreshFailure(ILogger logger, Exception exception);

    private void ChangeProvince(ChangeEventArgs args)
    {
        _province = args.Value?.ToString() ?? "";
        _timeZoneId = "";
    }

    private static string ProvinceName(string province) => province switch
    {
        "BC" => "British Columbia (BC)",
        "AB" => "Alberta (AB)",
        "SK" => "Saskatchewan (SK)",
        "MB" => "Manitoba (MB)",
        _ => province
    };

    private static string FormatUtc(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
        ?? "Not supplied";

    private static string FormatLocal(DateTimeOffset? value, string timeZoneId)
    {
        if (value is not { } instant) { return "Not supplied"; }
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone)) { return "Local rules unavailable"; }
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_polling is not null) { await _polling; }
        if (_completionTask is not null) { await _completionTask; }
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string PreviewChange(DateTimeOffset? current, DateTimeOffset? proposed)
    {
        if (current is not { } start || proposed is not { } end) { return "Change unavailable"; }
        var minutes = (end - start).TotalMinutes;
        return minutes == 0 ? "0 minutes (same time)"
            : minutes.ToString("+0.##;-0.##", CultureInfo.InvariantCulture) + " minutes (" + (minutes > 0 ? "later)" : "earlier)");
    }

    // Keep this route independent of the demo layout and its fictional account status.
    public sealed class ConnectionLayout : LayoutComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Body);
    }
}
