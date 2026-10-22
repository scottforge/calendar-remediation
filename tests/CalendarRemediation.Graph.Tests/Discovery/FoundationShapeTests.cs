using System.Text.Json;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;
using CalendarRemediation.Testing;

namespace CalendarRemediation.Graph.Tests.Discovery;

public sealed class FoundationShapeTests
{
    public enum ResponseTarget { Identity, Calendar, WindowsTimeZones, IanaTimeZones }

    private static ReadProbeOptions ProbeScope => new(ContractFixture.Identity.TenantId, ContractFixture.Identity.MailboxId,
        ContractFixture.Scope.Start, ContractFixture.Scope.End, 100, 10, 100, 10);

    public static IEnumerable<object[]> MalformedFoundationResponses
    {
        get
        {
            (ResponseTarget Target, string Json)[] cases =
            [
                (ResponseTarget.Identity, "[]"),
                (ResponseTarget.Calendar, "null"),
                (ResponseTarget.Calendar, "{\"id\":\"calendar\",\"isDefaultCalendar\":true}"),
                (ResponseTarget.Calendar, "{\"id\":\"calendar\",\"isDefaultCalendar\":true,\"owner\":[]}"),
                (ResponseTarget.Calendar, "{\"id\":\"calendar\",\"isDefaultCalendar\":true,\"owner\":{\"address\":42}}"),
                (ResponseTarget.Calendar, "{\"id\":\"calendar\",\"owner\":{\"address\":\"owner@example.test\"}}"),
                (ResponseTarget.Calendar, "{\"id\":\"calendar\",\"isDefaultCalendar\":\"true\",\"owner\":{\"address\":\"owner@example.test\"}}"),
                (ResponseTarget.WindowsTimeZones, "[]"),
                (ResponseTarget.WindowsTimeZones, "{}"),
                (ResponseTarget.WindowsTimeZones, "{\"value\":{}}"),
                (ResponseTarget.WindowsTimeZones, "{\"value\":[]}"),
                (ResponseTarget.WindowsTimeZones, "{\"value\":null}"),
                (ResponseTarget.IanaTimeZones, "[]"),
                (ResponseTarget.IanaTimeZones, "{}"),
                (ResponseTarget.IanaTimeZones, "{\"value\":{}}")
            ];
            foreach (var (target, json) in cases)
            {
                foreach (var probe in new[] { false, true }) { yield return [probe, target, json]; }
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedFoundationResponses))]
    public async Task MalformedFoundationStopsWithoutReportingAnEmptyCompleteScope(bool probe, ResponseTarget target, string json)
    {
        using var handler = new ScriptedGraphHandler();
        (ResponseTarget Target, string Address, object Body)[] foundation =
        [
            (ResponseTarget.Identity, GraphApi.IdentityUrl, (object)new { id = ContractFixture.Identity.MailboxId, mail = "owner@example.test" }),
            (ResponseTarget.Calendar, GraphApi.DefaultCalendarUrl, new { id = "calendar", isDefaultCalendar = true, owner = new { address = "owner@example.test" } }),
            (ResponseTarget.WindowsTimeZones, GraphApi.WindowsTimeZonesUrl, new { value = new[] { new { alias = "UTC" } } }),
            (ResponseTarget.IanaTimeZones, GraphApi.IanaTimeZonesUrl, new { value = new[] { new { alias = "Etc/UTC" } } })
        ];
        var malformedAddress = foundation.Single(response => response.Target == target).Address;
        foreach (var response in foundation)
        {
            if (response.Target == target) { break; }
            handler.Add(response.Body);
        }
        handler.Add(JsonSerializer.Deserialize<JsonElement>(json), inspect: request => Assert.Equal(malformedAddress, request.RequestUri!.AbsoluteUri));
        using var client = new HttpClient(handler);

        if (probe)
        {
            var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(ProbeScope);
            Assert.False(result.Complete);
            Assert.NotEmpty(result.Holds);
            Assert.Empty(result.Events);
            if (target is ResponseTarget.WindowsTimeZones or ResponseTarget.IanaTimeZones) { Assert.Equal("calendar", result.CalendarId); }
        }
        else
        {
            var result = await ReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);
            Assert.False(result.Complete);
            Assert.NotEmpty(result.Holds);
            Assert.Empty(result.Events);
            if (target == ResponseTarget.IanaTimeZones) { Assert.Contains("UTC", result.SupportedZones); }
        }
        var forbiddenAddress = target switch
        {
            ResponseTarget.Identity => GraphApi.DefaultCalendarUrl,
            ResponseTarget.Calendar => GraphApi.WindowsTimeZonesUrl,
            ResponseTarget.WindowsTimeZones => GraphApi.IanaTimeZonesUrl,
            ResponseTarget.IanaTimeZones => GraphApi.CalendarView(ContractFixture.Scope.Start, ContractFixture.Scope.End),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        Assert.Contains(malformedAddress, handler.Addresses);
        Assert.DoesNotContain(forbiddenAddress, handler.Addresses);
    }

    [Fact]
    public async Task MalformedZoneAfterValidAliasRetainsTheAliasAndStopsBeforeTheNextRequest()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(new { id = ContractFixture.Identity.MailboxId, mail = "owner@example.test" });
        handler.Add(new { id = "calendar", isDefaultCalendar = true, owner = new { address = "owner@example.test" } });
        handler.Add(JsonSerializer.Deserialize<JsonElement>("{\"value\":[{\"alias\":\"UTC\"},{\"alias\":42}]}"));
        using var client = new HttpClient(handler);

        var result = await ReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.NotEmpty(result.Holds);
        Assert.Equal("UTC", Assert.Single(result.SupportedZones));
        Assert.Empty(result.Events);
        Assert.DoesNotContain(GraphApi.IanaTimeZonesUrl, handler.Addresses);
    }

    [Theory]
    [InlineData(false, false, "identity")]
    [InlineData(false, true, "page")]
    [InlineData(false, false, "detail")]
    [InlineData(true, true, "identity")]
    [InlineData(true, false, "page")]
    [InlineData(true, true, "detail")]
    public async Task UnexpectedProgrammingFaultPropagatesWithoutContinuing(bool probe, bool missingKey, string stage)
    {
        using var handler = new ScriptedGraphHandler();
        if (stage != "identity") { handler.AddReadFoundation(ContractFixture.Identity.MailboxId); }
        if (stage == "detail") { handler.Add(new { value = new[] { ScriptedGraphHandler.Event() } }); }
        Exception fault = missingKey ? new KeyNotFoundException("unexpected internal fault") : new InvalidOperationException("unexpected internal fault");
        handler.AddFailure(fault);
        using var client = new HttpClient(handler);

        var actual = await Record.ExceptionAsync(async () =>
        {
            if (probe) { await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(ProbeScope); }
            else { await ReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope); }
        });

        Assert.Same(fault, actual);
        var faultAddress = stage switch
        {
            "identity" => GraphApi.IdentityUrl,
            "page" => GraphApi.CalendarView(ContractFixture.Scope.Start, ContractFixture.Scope.End),
            "detail" => probe ? GraphApi.ProbeEventDetail("event") : GraphApi.CalendarEventDetail("event"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        Assert.Equal(faultAddress, handler.Addresses.Last());
    }

    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(false, "io")]
    [InlineData(false, "http")]
    [InlineData(true, "timeout")]
    [InlineData(true, "io")]
    [InlineData(true, "http")]
    public async Task RequestFailureRetainsFoundationAndReturnsIncomplete(bool probe, string failure)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.AddFailure(failure switch
        {
            "io" => new IOException("response stream failed"),
            "http" => new HttpRequestException("connection failed"),
            _ => new TaskCanceledException("request timed out")
        });
        using var client = new HttpClient(handler);

        if (probe)
        {
            var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(ProbeScope);
            Assert.False(result.Complete);
            Assert.NotEmpty(result.Holds);
            Assert.Equal("calendar", result.CalendarId);
            Assert.Equal(2, result.SupportedZones.Length);
            Assert.Equal(4, result.Exchanges.Length);
        }
        else
        {
            var result = await ReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);
            Assert.False(result.Complete);
            Assert.Equal(failure == "timeout" ? "duration-budget" : "read-unavailable", Assert.Single(result.Holds));
            Assert.Contains("UTC", result.SupportedZones);
            Assert.Contains("Etc/UTC", result.SupportedZones);
        }
        Assert.Equal(GraphApi.CalendarView(ContractFixture.Scope.Start, ContractFixture.Scope.End), handler.Addresses.Last());
    }
}
