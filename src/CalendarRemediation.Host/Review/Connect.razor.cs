using System.Globalization;
using CalendarRemediation.Host.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;

namespace CalendarRemediation.Host.Review;

public partial class Connect : IAsyncDisposable
{
    [Inject] private LocalConnectionService Connection { get; set; } = null!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = null!;
    [Inject] private ILogger<Connect> Logger { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _polling;
    private ConnectionState? _state;
    private string? _refreshError;
    private string _province = "";
    private string _community = "";
    private string _timeZoneId = "";
    private bool _disposed;

    private IEnumerable<string> AvailableZones => LocalConnectionService.LocationZones.TryGetValue(_province, out var zones)
        ? zones : [];

    private string StatusText => _refreshError is not null ? "Connection status could not be verified. Live data has been cleared."
        : _state?.Phase switch
        {
            ConnectionPhase.Disabled => "Connection is unavailable on this installation.",
            ConnectionPhase.SignedOut => "Not connected. Start or resume sign-in below.",
            ConnectionPhase.SigningIn => "Sign-in is in progress in the system browser.",
            ConnectionPhase.AwaitingCompletion => "Verification succeeded. Complete sign-in below.",
            ConnectionPhase.Connected => "Connected to a verified mailbox with read-only access.",
            ConnectionPhase.Discovering => "Connected. Calendar discovery is running.",
            ConnectionPhase.Failed => "The operation failed. Review the error and retry below.",
            _ => "Checking local connection status…"
        };

    protected override Task OnInitializedAsync() => RefreshAsync(_lifetime.Token);

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        // Start only on the interactive instance, never during server prerendering.
        if (firstRender && !_disposed) { _polling = PollAsync(_lifetime.Token); }
        return Task.CompletedTask;
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

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_polling is not null) { await _polling; }
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    // Keep this route independent of the demo layout and its fictional account status.
    public sealed class ConnectionLayout : LayoutComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Body);
    }
}
