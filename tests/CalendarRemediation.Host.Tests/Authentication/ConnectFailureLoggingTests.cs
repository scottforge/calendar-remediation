using System.Collections.Concurrent;
using System.Security.Claims;
using CalendarRemediation.Host.Authentication;
using CalendarRemediation.Host.Review;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class ConnectFailureLoggingTests
{
    [Fact]
    public async Task RefreshFailureLogsOnlySafeCategoryAndNoException()
    {
        const string sensitive = "account@example.test https://example.test/?token=secret";
        var logger = new CapturingLogger<Connect>();
        await using var harness = new ConnectionHarness();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILogger<Connect>>(logger)
            .AddSingleton(harness.Service)
            .AddSingleton<AuthenticationStateProvider>(new FailingAuthenticationStateProvider(sensitive))
            .AddSingleton<AntiforgeryStateProvider, TestAntiforgeryStateProvider>()
            .AddSingleton<IJSRuntime, NoJavaScriptRuntime>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<Connect>());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(7101, entry.EventId.Id);
        Assert.Contains("refresh", entry.Message);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(sensitive, entry.Message);
    }

    private sealed class FailingAuthenticationStateProvider(string sensitive) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromException<AuthenticationState>(new InvalidOperationException(sensitive));
    }

    private sealed class TestAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new("token", "__RequestVerificationToken");
    }

    private sealed class NoJavaScriptRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke browser JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke browser JavaScript.");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal sealed record Entry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new(logLevel, eventId, formatter(state, exception), exception));
    }
}
