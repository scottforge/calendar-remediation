using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/SmokeCheck -- <publish-directory>");
    return 1;
}

var publishDirectory = Path.GetFullPath(args[0]);
var executable = Path.Combine(publishDirectory,
    OperatingSystem.IsWindows() ? "CalendarRemediation.Host.exe" : "CalendarRemediation.Host");
if (!File.Exists(executable))
{
    Console.Error.WriteLine($"Published apphost not found: {executable}");
    return 1;
}

// The application explicitly binds localhost:5009. Refuse to test an existing server.
foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
{
    if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
    {
        continue;
    }

    using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    try
    {
        socket.Bind(new IPEndPoint(address, 5009));
    }
    catch (SocketException exception)
    {
        Console.Error.WriteLine($"Cannot reserve localhost:5009. Stop any existing host first. {exception.Message}");
        return 1;
    }
}

using var process = new Process
{
    StartInfo = new ProcessStartInfo(executable)
    {
        WorkingDirectory = publishDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Production", ["DOTNET_ENVIRONMENT"] = "Production", ["Feasibility__EvidencePath"] = "" }
    }
};
var output = new StringBuilder();
var error = new StringBuilder();
Task outputPump = Task.CompletedTask;
Task errorPump = Task.CompletedTask;
var started = false;
var result = 1;
try
{
    started = process.Start();
    if (!started)
    {
        throw new InvalidOperationException("Could not start the published application.");
    }

    outputPump = CaptureAsync(process.StandardOutput, output);
    errorPump = CaptureAsync(process.StandardError, error);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
    using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5009"), Timeout = TimeSpan.FromSeconds(2) };
    var html = await WaitForShellAsync(process, client, timeout.Token);
    if (!html.Contains("<title>Calendar Remediation", StringComparison.Ordinal)
        || !html.Contains("data-workspace=\"calendar-remediation\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The home page did not contain the expected application shell.");
    }

    // Resolve fingerprinted URLs from the rendered page rather than guessing asset names.
    var assetPaths = Regex.Matches(html, "(?:href|src)=\"([^\"]+)\"", RegexOptions.CultureInvariant)
        .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
        .Where(path => path.EndsWith(".css", StringComparison.Ordinal) || path.Contains("_framework/blazor.web.", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (assetPaths.Count(path => path.EndsWith(".css", StringComparison.Ordinal)) < 2
        || !assetPaths.Any(path => path.Contains("_framework/blazor.web.", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("The shell did not reference both stylesheets and the Blazor framework script.");
    }

    foreach (var path in assetPaths)
    {
        var uri = new Uri(client.BaseAddress, path);
        if (uri.Host != client.BaseAddress.Host || uri.Port != client.BaseAddress.Port)
        {
            throw new InvalidOperationException($"Unexpected external asset: {path}");
        }

        using var response = await client.GetAsync(uri, timeout.Token);
        var expectedType = path.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "javascript";
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (response.StatusCode != HttpStatusCode.OK || mediaType is null
            || !mediaType.Contains(expectedType, StringComparison.Ordinal)
            || (await response.Content.ReadAsByteArrayAsync(timeout.Token)).Length == 0)
        {
            throw new InvalidOperationException($"Asset check failed: {path} ({response.StatusCode}, {mediaType}).");
        }
    }

    var feasibility = await client.GetStringAsync("/feasibility", timeout.Token);
    if (!feasibility.Contains("Calendar pilot feasibility", StringComparison.Ordinal)
        || !feasibility.Contains("Blocked", StringComparison.Ordinal)
        || !feasibility.Contains("exit allowed: False", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Default feasibility status did not remain blocked.");
    }

    if (process.HasExited)
    {
        throw new InvalidOperationException($"Published host exited with code {process.ExitCode}.");
    }

    Console.WriteLine($"Published shell, blocked feasibility status and {assetPaths.Length} assets passed. This does not test browser interactivity or calendar behavior.");
    result = 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Smoke check failed: {exception.Message}");
}
finally
{
    if (started)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The host exited between the status check and termination.
                }
            }

            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupTimeout.Token);
            await Task.WhenAll(outputPump, errorPump).WaitAsync(cleanupTimeout.Token);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Host cleanup failed: {exception.Message}");
            result = 1;
        }
    }

    if (result != 0 && outputPump.IsCompleted && errorPump.IsCompleted)
    {
        Console.Error.WriteLine(output.ToString());
        Console.Error.WriteLine(error.ToString());
    }
}

return result;

static async Task<string> WaitForShellAsync(Process process, HttpClient client, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"Published host exited with code {process.ExitCode} during startup.");
        }

        try
        {
            using var response = await client.GetAsync("/", cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
        catch (HttpRequestException)
        {
            // Connection refusal is expected until Kestrel starts listening.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An individual HTTP request timed out within the overall startup deadline.
        }

        await Task.Delay(200, cancellationToken);
    }

    throw new TimeoutException("Published host was not ready within 30 seconds.");
}

static async Task CaptureAsync(StreamReader reader, StringBuilder log)
{
    var buffer = new char[1024];
    int count;
    while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
    {
        log.Append(buffer, 0, count);
        if (log.Length > 16_384)
        {
            log.Remove(0, log.Length - 16_384);
        }
    }
}
