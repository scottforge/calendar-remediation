using System.Collections.Concurrent;
using CalendarRemediation.Host.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using static CalendarRemediation.Host.Tests.Authentication.ConnectionHarness;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class ConnectionFailureLoggingTests
{
    private const string Sensitive = "private-account@example.test oauth-code token-value tenant-secret";

    [Theory]
    [InlineData("msal", LogLevel.Warning, "sign-in-failed", "sign-in-failed")]
    [InlineData("interaction", LogLevel.Warning, "interaction-required", "interaction-required")]
    [InlineData("identity", LogLevel.Warning, "identity-validation-failed", "connection-failed")]
    [InlineData("known", LogLevel.Warning, "identity-mismatch", "identity-mismatch")]
    [InlineData("unknown-code", LogLevel.Warning, "connection-rejected", Sensitive)]
    [InlineData("cache", LogLevel.Error, "account-cache-unavailable", "account-cache-unavailable")]
    [InlineData("timeout", LogLevel.Warning, "operation-timeout-or-provider-cancelled", "connection-failed")]
    [InlineData("provider-cancel", LogLevel.Warning, "operation-timeout-or-provider-cancelled", "cancelled")]
    [InlineData("unexpected", LogLevel.Error, "unexpected-fault", "connection-failed")]
    public async Task WorkerFailuresAreObservedWithSafeCategoriesAndPreserveProblemMappings(
        string failure, LogLevel level, string reason, string problem)
    {
        var logger = new FailureLogger<LocalConnectionService>();
        Exception exception = failure switch
        {
            "msal" => new MsalClientException(Sensitive, Sensitive),
            "interaction" => new MsalUiRequiredException(Sensitive, Sensitive),
            "identity" => new InvalidOperationException(Sensitive),
            "known" => new ConnectionException(new("identity-mismatch", Sensitive)),
            "unknown-code" => new ConnectionException(new(Sensitive, Sensitive)),
            "cache" => new IOException(Sensitive),
            "timeout" => new TimeoutException(Sensitive),
            "provider-cancel" => new OperationCanceledException(Sensitive),
            _ => new NotSupportedException(Sensitive)
        };
        var identity = new FakeIdentity { Authenticate = (_, _) => Task.FromException<SignInObservation>(exception) };
        await using var service = new LocalConnectionService(identity, new FakeCalendar(), logger);
        var bootstrap = service.Bootstrap(new());
        var ticket = service.StartSignIn(Principal(bootstrap), bootstrap.Version);
        var state = await WaitAsync(service, ticket, ConnectionPhase.Failed);
        Assert.Equal(problem, state.Problem?.Code);
        var entry = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(level, entry.Level);
        Assert.Contains(reason, entry.Message);
        Assert.Contains("sign-in", entry.Message);
        Assert.Contains(exception.GetType().Name, entry.Message);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(Sensitive, entry.Message);
        Assert.DoesNotContain(ticket.Id, entry.Message);
        Assert.DoesNotContain(ticket.Version, entry.Message);
        var scope = Assert.Single(logger.Scopes);
        var operationId = Assert.IsType<Guid>(Assert.Single(scope).Value);
        Assert.NotEqual(Guid.Empty, operationId);
        Assert.Equal(operationId, entry.OperationId);
        Assert.Contains(operationId.ToString(), entry.Message);
        Assert.Equal(operationId, entry.Fields["ConnectionOperationId"]);
        var start = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains("silent-continuation started", start.Message);
        Assert.Equal(operationId, start.OperationId);
    }

    [Fact]
    public async Task UserCancellationAndSuccessfulPollingAreQuiet()
    {
        var logger = new FailureLogger<LocalConnectionService>();
        var gate = new Gate<SignInObservation>();
        var identity = new FakeIdentity { Authenticate = (_, ct) => gate.RunAsync(ct) };
        await using var service = new LocalConnectionService(identity, new FakeCalendar(), logger);
        var bootstrap = service.Bootstrap(new());
        await service.GetStateAsync(Principal(bootstrap));
        await service.GetStateAsync(Principal(bootstrap));
        Assert.Empty(logger.Entries);
        var ticket = service.StartSignIn(Principal(bootstrap), bootstrap.Version);
        await gate.WaitForEntryAsync();
        var operationRevision = (await service.GetStateAsync(Principal(ticket))).OperationRevision;
        service.Cancel(Principal(ticket), ticket.Version, operationRevision);
        await service.DisposeAsync();
        var start = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, start.Level);
        Assert.Contains("silent-continuation started", start.Message);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("crypto")]
    public async Task CachedAccountFailuresAreLoggedAsErrorsOnceAcrossPolling(string failure)
    {
        var logger = new FailureLogger<LocalConnectionService>();
        var identity = new CacheFailingIdentity(failure);
        await using var service = new LocalConnectionService(identity, new FakeCalendar(), logger);
        var bootstrap = service.Bootstrap(new());
        var principal = Principal(bootstrap);

        var first = await service.GetStateAsync(principal);
        var second = await service.GetStateAsync(principal);
        var third = await service.GetStateAsync(principal);

        Assert.Equal("account-cache-unavailable", first.Problem?.Code);
        Assert.Equal(first.Problem, second.Problem);
        Assert.Equal(first.Problem, third.Problem);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("cached-account", entry.Message);
        Assert.Contains("account-cache-unavailable", entry.Message);
        var operationId = Assert.IsType<Guid>(entry.Fields["ConnectionOperationId"]);
        Assert.NotEqual(Guid.Empty, operationId);
        Assert.Contains(operationId.ToString(), entry.Message);
        Assert.DoesNotContain(Sensitive, entry.Message);
        Assert.Null(entry.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateWorkerFaultsAreObservedButDiscardedTimeoutsAreQuiet(bool timeout)
    {
        var logger = new FailureLogger<LocalConnectionService>();
        var gate = new TaskCompletionSource<SignInObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new FakeIdentity { Authenticate = (_, _) => { entered.SetResult(); return gate.Task; } };
        await using var service = new LocalConnectionService(identity, new FakeCalendar(), logger);
        var bootstrap = service.Bootstrap(new());
        service.StartSignIn(Principal(bootstrap), bootstrap.Version);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = service.Bootstrap(new());
        gate.SetException(timeout ? new TimeoutException(Sensitive) : new NotSupportedException(Sensitive));
        await service.DisposeAsync();
        if (timeout) { Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning); }
        else
        {
            var entry = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.DoesNotContain(Sensitive, entry.Message);
        }
        Assert.NotEqual(bootstrap.Id, replacement.Id);
    }

    internal static async Task<ConnectionState> WaitAsync(LocalConnectionService service, ConnectionTicket ticket, ConnectionPhase phase)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = await service.GetStateAsync(Principal(ticket), deadline.Token);
            if (state.Phase == phase) { return state; }
            await Task.Yield();
        }
    }

    private sealed class CacheFailingIdentity(string failure) : ILocalIdentityClient
    {
        public ConnectionProblem? Unavailable => null;
        public Task<string?> GetCachedAccountAsync(CancellationToken cancellationToken) =>
            Task.FromException<string?>(failure switch
            {
                "access" => new UnauthorizedAccessException(Sensitive),
                "crypto" => new System.Security.Cryptography.CryptographicException(Sensitive),
                _ => new IOException(Sensitive)
            });
        public Task<SignInObservation> AuthenticateAsync(bool interactive, BrowserSignInContext? browser,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> AcquireReadTokenAsync(SignInObservation account, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task ClearAsync() => throw new NotSupportedException();
        public void Dispose() { }
    }
}

internal sealed class FailureLogger<T> : ILogger<T>
{
    internal sealed record Entry(LogLevel Level, string Message, Exception? Exception, Guid? OperationId,
        IReadOnlyDictionary<string, object?> Fields);
    private readonly AsyncLocal<Guid?> _operationId = new();
    public ConcurrentQueue<Entry> Entries { get; } = new();
    public ConcurrentQueue<KeyValuePair<string, object>[]> Scopes { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var previous = _operationId.Value;
        if (state is IEnumerable<KeyValuePair<string, object>> values)
        {
            var fields = values.ToArray();
            Scopes.Enqueue(fields);
            if (fields.FirstOrDefault(field => field.Key == "ConnectionOperationId").Value is Guid operationId)
            {
                _operationId.Value = operationId;
            }
        }
        return new Scope(() => _operationId.Value = previous);
    }
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue(new(logLevel, formatter(state, exception), exception, _operationId.Value,
            state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToDictionary() : new Dictionary<string, object?>()));

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
