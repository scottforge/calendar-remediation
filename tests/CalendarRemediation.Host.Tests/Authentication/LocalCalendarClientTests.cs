using System.Net;
using System.Text;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Host.Authentication;
using Microsoft.Extensions.Logging;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class LocalCalendarClientTests
{
    private const string ProviderRequestId = "d21d1065-9434-465e-9a0d-27a6c5c12b51";
    private const string Mailbox = """{"id":"mailbox-a","mail":"owner@example.test","displayName":"Private Display Name"}""";

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":\" \"}")]
    public async Task VerifyConsumerRejectsMissingMailboxIdentity(string me)
    {
        using var handler = new ScriptedHandler(false, 200, me, null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(new RecordingLogger(), http);
        var account = ConnectionHarness.Account with { TenantId = "9188040d-6c67-4c5b-b112-36a304b66dad" };

        var error = await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(account,
            _ => Task.FromResult("selected-account-token"), CancellationToken.None));

        Assert.Equal("identity-mismatch", error.Problem.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("another-alias@example.test", true)]
    [InlineData("owner@example.test", false)]
    public async Task VerifyConsumerRequiresDefaultCalendarAndOwner(string owner, bool isDefault)
    {
        var logger = new RecordingLogger();
        var calendar = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "calendar-a",
            owner = new { address = owner },
            isDefaultCalendar = isDefault
        });
        using var handler = new ScriptedHandler(true, 200, calendar, null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);
        var account = ConnectionHarness.Account with { TenantId = "9188040d-6c67-4c5b-b112-36a304b66dad" };

        if (isDefault)
        {
            var verified = await client.VerifyAsync(account,
                _ => Task.FromResult("selected-account-token"), CancellationToken.None);
            Assert.Equal("calendar-a", verified.Identity.CalendarId);
            Assert.Empty(logger.Entries);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(account,
                _ => Task.FromResult("selected-account-token"), CancellationToken.None));
            Assert.Equal("calendar-mismatch", error.Problem.Code);
        }
    }

    [Fact]
    public async Task VerifyConsumerStopsBeforeCalendarDispatchWhenBoundTokenAcquisitionFails()
    {
        using var handler = new ScriptedHandler(false, 200, Mailbox, null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(new RecordingLogger(), http);
        var account = ConnectionHarness.Account with { TenantId = "9188040d-6c67-4c5b-b112-36a304b66dad" };
        var tokens = 0;
        Task<string> BoundToken(CancellationToken _)
        {
            if (++tokens == 2) { throw new InvalidOperationException("Account binding changed."); }
            return Task.FromResult("selected-account-token");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.VerifyAsync(account, BoundToken, CancellationToken.None));

        Assert.Equal(2, tokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("9188040d-6c67-4c5b-b112-36a304b66dad", "opaque-msal-subject", true)]
    [InlineData("11111111-1111-1111-1111-111111111111", "different-object", false)]
    public async Task VerifyConsumerUsesGraphMailboxIdAndOrganizationRequiresMatchingObjectId(string tenant,
        string subject, bool accepted)
    {
        const string calendar = """{"id":"calendar-a","owner":{"address":"owner@example.test"},"isDefaultCalendar":true}""";
        using var handler = new ScriptedHandler(true, 200, calendar, null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(new RecordingLogger(), http);
        var account = ConnectionHarness.Account with { TenantId = tenant, MailboxObjectId = subject };
        var tokens = 0;
        Task<string> BoundToken(CancellationToken _) { tokens++; return Task.FromResult("selected-account-token"); }

        if (accepted)
        {
            var verified = await client.VerifyAsync(account, BoundToken, CancellationToken.None);
            Assert.Equal("mailbox-a", verified.Identity.MailboxId);
            Assert.Equal(tenant, verified.Identity.TenantId);
            Assert.Equal(2, tokens);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(account, BoundToken, CancellationToken.None));
            Assert.Equal("identity-mismatch", error.Problem.Code);
            Assert.Equal(1, tokens);
        }
    }

    [Theory]
    [InlineData(false, 403, "{\"error\":{\"message\":\"private-provider-message\"}}", "missing")]
    [InlineData(true, 503, "private-provider-message", "missing")]
    [InlineData(false, 200, "", "missing")]
    [InlineData(true, 200, "not-json-private-provider-message", "invalid-json")]
    [InlineData(false, 200, "[]", "nonobject")]
    [InlineData(true, 200, "null", "nonobject")]
    [InlineData(false, 201, "{}", "object")]
    public async Task VerifyUnavailableLogsSafeStructuredDiagnostic(bool calendar, int status, string body, string shape)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler(calendar, status, body, ProviderRequestId, "120");
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);

        var error = await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(
            ConnectionHarness.Account, _ => Task.FromResult("private-access-token"), CancellationToken.None));

        Assert.Equal("graph-unavailable", error.Problem.Code);
        Assert.Equal("Microsoft 365 identity verification was unavailable. Try signing in again.", error.Problem.Message);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(calendar ? "default-calendar" : "mailbox", entry.Fields["Operation"]);
        Assert.Equal(status, entry.Fields["Status"]);
        Assert.Equal(handler.FailedClientRequestId, entry.Fields["ClientRequestId"]);
        Assert.Equal(ProviderRequestId, entry.Fields["ProviderRequestId"]);
        Assert.Equal("120", entry.Fields["RetryAfter"]);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(body), entry.Fields["ResponseBytes"]);
        Assert.Equal(shape, entry.Fields["ResponseShape"]);
        Assert.Equal(calendar ? 2 : 1, handler.Calls);
        foreach (var secret in new[] { "private-access-token", "private-provider-message", "owner@example.test",
            "Private Display Name", "tenant-a", "mailbox-a", "home-a", "$select", "https://" })
        {
            Assert.DoesNotContain(secret, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, string.Join(" ", entry.Fields.Values), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(null, null, "unavailable", "unavailable")]
    [InlineData("private-owner@example.test", "private-retry-value", "unavailable", "unavailable")]
    [InlineData("D21D1065-9434-465E-9A0D-27A6C5C12B51", "999999999", ProviderRequestId, "86400")]
    [InlineData(ProviderRequestId, "0", ProviderRequestId, "0")]
    [InlineData(ProviderRequestId, "Wed, 21 Oct 2015 07:28:00 GMT", ProviderRequestId, "Wed, 21 Oct 2015 07:28:00 GMT")]
    public async Task VerifyUnavailableNormalizesUntrustedHeaders(string? providerId, string? retryAfter,
        string expectedProviderId, string expectedRetryAfter)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler(false, 429, "", providerId, retryAfter);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);

        await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(
            ConnectionHarness.Account, _ => Task.FromResult("synthetic-token"), CancellationToken.None));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedProviderId, entry.Fields["ProviderRequestId"]);
        Assert.Equal(expectedRetryAfter, entry.Fields["RetryAfter"]);
        Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeLeavesCallerOwnedClientUsable()
    {
        using var handler = new ScriptedHandler(false, 200, Mailbox, null, null);
        using var http = new HttpClient(handler);
        var client = new LocalCalendarClient(new RecordingLogger(), http);

        client.Dispose();

        // Another consumer can still use the shared client after the calendar client is disposed.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/shared-client");
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Mailbox, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task VerifySuccessLogsSafeNetworkInformationWithoutWarnings()
    {
        var logger = new RecordingLogger();
        const string calendar = """{"id":"calendar-a","owner":{"address":"owner@example.test"},"isDefaultCalendar":true}""";
        using var handler = new ScriptedHandler(true, 200, calendar, ProviderRequestId, null);
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, handler));
        using var client = new LocalCalendarClient(logger, http);

        var mailbox = await client.VerifyAsync(ConnectionHarness.Account,
            _ => Task.FromResult("synthetic-token"), CancellationToken.None);

        Assert.Equal("calendar-a", mailbox.Identity.CalendarId);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            AssertPrivate(entry);
        });
    }

    [Theory]
    [InlineData("{\"owner\":{\"address\":\"owner@example.test\"},\"isDefaultCalendar\":true}", false, true, true, "True", true, true, false)]
    [InlineData("{\"id\":\"id-secret\",\"isDefaultCalendar\":true}", true, false, true, "True", true, false, false)]
    [InlineData("{\"id\":\"id-secret\",\"owner\":{\"address\":\"owner@example.test\"}}", true, true, false, "Undefined", false, true, false)]
    [InlineData("{\"id\":\"id-secret\",\"owner\":{\"address\":\"owner@example.test\"},\"isDefaultCalendar\":null}", true, true, true, "Null", false, true, false)]
    [InlineData("{\"id\":\"id-secret\",\"owner\":{\"address\":\"owner@example.test\"},\"isDefaultCalendar\":\"true\"}", true, true, true, "String", false, true, false)]
    [InlineData("{\"id\":\"id-secret\",\"owner\":{\"address\":\"owner@example.test\"},\"isDefaultCalendar\":false}", true, true, true, "False", false, true, false)]
    [InlineData("{\"id\":\"id-secret\",\"owner\":{\"address\":\"alias-secret@example.test\"},\"isDefaultCalendar\":true}", true, true, true, "True", true, false, false)]
    public async Task CalendarMismatchReportsEveryPredicate(string calendar, bool idPresent, bool ownerPresent,
        bool defaultPresent, string kind, bool isDefault, bool matchesMail, bool matchesUpn)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler(true, 200, calendar, null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);

        var error = await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(ConnectionHarness.Account,
            _ => Task.FromResult("token-secret"), CancellationToken.None));

        Assert.Equal("calendar-mismatch", error.Problem.Code);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(idPresent, entry.Fields["CalendarIdPresent"]);
        Assert.Equal(ownerPresent, entry.Fields["OwnerPresent"]);
        Assert.Equal(defaultPresent, entry.Fields["DefaultPresent"]);
        Assert.Equal(kind, entry.Fields["DefaultValueKind"]);
        Assert.Equal(isDefault, entry.Fields["IsDefault"]);
        Assert.Equal(matchesMail, entry.Fields["OwnerMatchesMail"]);
        Assert.Equal(matchesUpn, entry.Fields["OwnerMatchesUpn"]);
        AssertPrivate(entry);
    }

    [Theory]
    [InlineData(true, "{}", false)]
    [InlineData(false, "{}", false)]
    [InlineData(false, "{\"id\":\"id-secret\"}", true)]
    public async Task IdentityMismatchReportsOnlySafeBooleans(bool personal, string me, bool mailboxPresent)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler(false, 200, me, null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);
        var account = ConnectionHarness.Account with { TenantId = personal ? "9188040d-6c67-4c5b-b112-36a304b66dad" : "tenant-secret" };

        var error = await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(account,
            _ => Task.FromResult("token-secret"), CancellationToken.None));

        Assert.Equal("identity-mismatch", error.Problem.Code);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(personal, entry.Fields["PersonalAccount"]);
        Assert.Equal(mailboxPresent, entry.Fields["MailboxIdPresent"]);
        Assert.Equal(true, entry.Fields["AuthObjectIdPresent"]);
        Assert.Equal(false, entry.Fields["MailboxMatchesAuthObjectId"]);
        AssertPrivate(entry);
    }

    [Fact]
    public async Task UpnMatchIsReportedIndependentlyOfMailMatch()
    {
        var logger = new RecordingLogger();
        using var handler = new SequenceHandler("""{"id":"mailbox-a","mail":"mail-secret@example.test","userPrincipalName":"upn-secret@example.test"}""",
            """{"owner":{"address":"UPN-secret@example.test"},"isDefaultCalendar":true}""");
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);

        await Assert.ThrowsAsync<ConnectionException>(() => client.VerifyAsync(ConnectionHarness.Account,
            _ => Task.FromResult("token-secret"), CancellationToken.None));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(false, entry.Fields["OwnerMatchesMail"]);
        Assert.Equal(true, entry.Fields["OwnerMatchesUpn"]);
        AssertPrivate(entry);
    }

    [Fact]
    public async Task VerifyConsumerAcceptsCalendarOwnerMatchingSelectedAccountUsername()
    {
        var logger = new RecordingLogger();
        using var handler = new SequenceHandler(
            "{\"id\":\"mailbox-a\",\"mail\":\"mail-secret@example.test\",\"userPrincipalName\":\"upn-secret@example.test\"}",
            "{\"id\":\"calendar-a\",\"owner\":{\"address\":\"alias-secret@example.test\"},\"isDefaultCalendar\":true}");
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);
        var account = ConnectionHarness.Account with
        {
            TenantId = "9188040d-6c67-4c5b-b112-36a304b66dad",
            AccountUsername = "alias-secret@example.test"
        };

        var mailbox = await client.VerifyAsync(account, _ => Task.FromResult("token-secret"), CancellationToken.None);

        Assert.Equal("calendar-a", mailbox.Identity.CalendarId);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("network", LogLevel.Warning)]
    [InlineData("timeout", LogLevel.Warning)]
    [InlineData("json", LogLevel.Warning)]
    [InlineData("token", LogLevel.Warning)]
    [InlineData("unexpected", LogLevel.Error)]
    public async Task VerificationExceptionsAreLoggedWithoutExceptionDetails(string failure, LogLevel level)
    {
        var exception = failure switch
        {
            "network" => (Exception)new HttpRequestException("token-secret"),
            "timeout" => new TaskCanceledException("token-secret"),
            "json" => new System.Text.Json.JsonException("token-secret"),
            "token" => new Microsoft.Identity.Client.MsalClientException("token-secret", "owner@example.test"),
            _ => new InvalidOperationException("token-secret")
        };
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler(false, 200, Mailbox, null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);

        var actual = await Record.ExceptionAsync(() => client.VerifyAsync(ConnectionHarness.Account,
            _ => Task.FromException<string>(exception), CancellationToken.None));

        Assert.Same(exception, actual);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Equal("token-acquisition", entry.Fields["Operation"]);
        AssertPrivate(entry);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotLog()
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient();
        using var client = new LocalCalendarClient(logger, http);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.VerifyAsync(ConnectionHarness.Account,
            ct => Task.FromCanceled<string>(ct), cancellation.Token));

        Assert.Empty(logger.Entries);
    }

    private static void AssertPrivate(LogEntry entry)
    {
        Assert.Null(entry.Exception);
        var text = entry.Message + string.Join(" ", entry.Fields.Values);
        foreach (var secret in new[] { "secret", "owner@example.test", "mailbox-a", "tenant-a", "Private Display Name", "https://", "$select" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("network", true, false)]
    [InlineData("timeout", false, true)]
    public async Task DiscoveryIncompleteFailuresProduceSafeWarning(string body, bool unavailable, bool durationBudget)
    {
        var logger = new RecordingLogger();
        using var handler = new SequenceHandler(body);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);

        var result = await client.DiscoverAsync(Scope(), _ => Task.FromException<string>(body == "timeout"
            ? new TaskCanceledException("token-secret") : new HttpRequestException("token-secret")), CancellationToken.None);

        Assert.False(result.Complete);
        var entry = logger.Entries.Last();
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(unavailable, entry.Fields["ReadUnavailable"]);
        Assert.Equal(durationBudget, entry.Fields["DurationBudgetExceeded"]);
        Assert.All(logger.Entries, AssertPrivate);
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("not-json-secret", false)]
    [InlineData("[]", true)]
    [InlineData("not-json-secret", true)]
    public async Task DiscoveryMalformedResponseWarnsAndRetainsIncompleteEvidence(string body, bool afterZones)
    {
        var logger = new RecordingLogger();
        using var handler = new SequenceHandler(afterZones ? [Mailbox,
            """{"id":"calendar-a","owner":{"address":"owner@example.test"},"isDefaultCalendar":true}""",
            """{"value":[{"alias":"UTC"}]}""", body] : [body]);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);
        var scope = Scope();

        var result = await client.DiscoverAsync(scope, _ => Task.FromResult("token-secret"), CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(scope, result.Scope);
        Assert.Equal("read-unavailable", Assert.Single(result.Holds));
        Assert.Empty(result.Events);
        Assert.Equal(afterZones ? ["UTC"] : Array.Empty<string>(), result.SupportedZones);
        Assert.Equal(0, result.Pages);
        Assert.Equal(0, result.DetailReads);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("read-unavailable", entry.Fields["HoldCategories"]);
        Assert.Equal(1, entry.Fields["HoldCount"]);
        Assert.Equal(0, entry.Fields["Pages"]);
        Assert.Equal(0, entry.Fields["DetailReads"]);
        Assert.Equal(true, entry.Fields["ReadUnavailable"]);
        Assert.Equal(false, entry.Fields["DurationBudgetExceeded"]);
        AssertPrivate(entry);
    }

    [Fact]
    public async Task SuccessfulDiscoveryLogsSafeNetworkInformationWithoutWarnings()
    {
        var logger = new RecordingLogger();
        using var handler = new SequenceHandler(Mailbox,
            """{"id":"calendar-a","owner":{"address":"owner@example.test"},"isDefaultCalendar":true}""",
            """{"value":[{"alias":"UTC"}]}""", """{"value":[{"alias":"Etc/UTC"}]}""", """{"value":[]}""");
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, handler));
        using var client = new LocalCalendarClient(logger, http);

        var result = await client.DiscoverAsync(Scope(), _ => Task.FromResult("token-secret"), CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(5, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            AssertPrivate(entry);
        });
    }

    [Fact]
    public async Task CallerSuppliedPipelineIsPreservedWithoutImplicitNetworkLogging()
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler(true, 200,
            """{"id":"calendar-a","owner":{"address":"owner@example.test"},"isDefaultCalendar":true}""", null, null);
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);

        await client.VerifyAsync(ConnectionHarness.Account, _ => Task.FromResult("token-secret"), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(true, "identity-mismatch")]
    [InlineData(false, "identity-mismatch")]
    [InlineData(true, "calendar-mismatch")]
    [InlineData(false, "calendar-mismatch")]
    public async Task DiscoveryMismatchLogsSafeHoldCategory(bool personal, string category)
    {
        var logger = new RecordingLogger();
        using var handler = new SequenceHandler(category == "identity-mismatch" ? "{\"id\":\"id-secret\"}" : Mailbox,
            """{"id":"id-secret","owner":{"address":"alias-secret@example.test"},"isDefaultCalendar":true}""");
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);
        var scope = Scope();
        if (personal) { scope = scope with { Identity = scope.Identity with { TenantId = "9188040d-6c67-4c5b-b112-36a304b66dad" } }; }

        var result = await client.DiscoverAsync(scope, _ => Task.FromResult("token-secret"), CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(category, Assert.Single(result.Holds));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(category, entry.Fields["HoldCategories"]);
        Assert.Equal(1, entry.Fields["HoldCount"]);
        Assert.Equal(0, entry.Fields["Pages"]);
        Assert.Equal(0, entry.Fields["DetailReads"]);
        AssertPrivate(entry);
    }

    [Theory]
    [InlineData("identity-mismatch")]
    [InlineData("calendar-mismatch")]
    [InlineData("duration-budget")]
    [InlineData("read-unavailable")]
    [InlineData("page-budget")]
    [InlineData("paging-cycle")]
    [InlineData("invalid-next-link")]
    [InlineData("event-budget")]
    [InlineData("invalid-response")]
    [InlineData("conflicting-observation")]
    [InlineData("detail-budget")]
    [InlineData("detail read unavailable")]
    public void DiscoveryLoggingAllowlistsKnownHoldsAndRedactsUnknownText(string category)
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient();
        using var client = new LocalCalendarClient(logger, http);
        var result = new DiscoveryResult(Scope(), false, [], [],
            [category, "calendar-mismatch\r\nowner@example.test token-secret https://private.test", "IDENTITY-MISMATCH", category], 3, 2);

        // Inject future/untrusted holds directly at the private logger boundary without widening the client API.
        var boundary = typeof(LocalCalendarClient).GetMethod("LogDiscoveryIncomplete",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(DiscoveryResult)]);
        Assert.NotNull(boundary);
        boundary.Invoke(client, [result]);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal($"{category}, unknown", entry.Fields["HoldCategories"]);
        Assert.Equal(4, entry.Fields["HoldCount"]);
        Assert.Equal(3, entry.Fields["Pages"]);
        Assert.Equal(2, entry.Fields["DetailReads"]);
        Assert.Equal(category == "duration-budget", entry.Fields["DurationBudgetExceeded"]);
        Assert.Equal(category == "read-unavailable", entry.Fields["ReadUnavailable"]);
        AssertPrivate(entry);
        Assert.DoesNotContain("\r", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidDiscoveryIsSilentIncludingConsumerOwnerBoundByMeRoute(bool personal)
    {
        var logger = new RecordingLogger();
        using var handler = new SequenceHandler(Mailbox,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                id = "calendar-a",
                owner = new { address = personal ? "alias-secret@example.test" : "owner@example.test" },
                isDefaultCalendar = true
            }), """{"value":[{"alias":"UTC"}]}""", """{"value":[{"alias":"Etc/UTC"}]}""", """{"value":[]}""");
        using var http = new HttpClient(handler);
        using var client = new LocalCalendarClient(logger, http);
        var scope = Scope();
        if (personal) { scope = scope with { Identity = scope.Identity with { TenantId = "9188040d-6c67-4c5b-b112-36a304b66dad" } }; }

        var result = await client.DiscoverAsync(scope, _ => Task.FromResult("token-secret"), CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task DiscoveryCallerCancellationDoesNotLog()
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient();
        using var client = new LocalCalendarClient(logger, http);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DiscoverAsync(Scope(),
            ct => Task.FromCanceled<string>(ct), cancellation.Token));

        Assert.Empty(logger.Entries);
    }

    private static DiscoveryScope Scope()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new(new("11111111-1111-1111-1111-111111111111", "mailbox-a", "calendar-a"),
            start, start.AddDays(1), start, new(10, 10, 10, TimeSpan.FromSeconds(30)));
    }

    private sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[_index++]) });
    }

    private sealed class ScriptedHandler(bool calendar, int status, string body, string? providerId, string? retryAfter) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Guid FailedClientRequestId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (calendar && Calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mailbox) });
            }
            FailedClientRequestId = Guid.Parse(Assert.Single(request.Headers.GetValues("client-request-id")));
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            if (providerId is not null) { response.Headers.TryAddWithoutValidation("request-id", providerId); }
            if (retryAfter is not null) { response.Headers.TryAddWithoutValidation("Retry-After", retryAfter); }
            return Task.FromResult(response);
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message, Dictionary<string, object?> Fields);

    private sealed class RecordingLogger : ILogger<LocalCalendarClient>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, exception, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }
}
