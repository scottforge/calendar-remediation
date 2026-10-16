# Calendar Remediation

Helps meeting organizers in British Columbia, Alberta, Saskatchewan, and Manitoba find meetings that should move to their confirmed local time.
Review the meetings you organized, choose which ones to correct, and approve the exact changes and attendee notifications before they reach your calendar.

## How it works

- Sign in with your Microsoft 365 work account and confirm your location.
- Review all meetings you organized over the next 90 days, with an optional date-range filter.
- Optionally ask Copilot to select meetings that may need a local-time correction, or batch update meetings yourself.
- Copilot will explain its choices and uncertainty, allowing you to make informed decisions.
- Review the exact changes and attendee notifications for selected meetings, then approve the updates.
- The app will use Microsoft Graph API to safely update your meetings and send notifications to attendees.
- Single meetings and recurring series are supported, while complex recurrence patterns are held for review.

### Microsoft sign-in

Authentication happens in your browser, where Microsoft asks you to sign in and grant the required calendar permissions.

- Grant accesses your calendar using the same account permissions.
- Approve the exact changes before it updates meetings you organize.

## Development

Install a stable `.NET 10 SDK` at or above the `10.0.100` baseline.
`NuGet` access is required for the initial restore.

### Build and run

Run these commands from the repository root:

```sh
dotnet restore CalendarRemediation.slnx
dotnet format CalendarRemediation.slnx --verify-no-changes --no-restore
dotnet build CalendarRemediation.slnx -c Release --no-restore
dotnet test CalendarRemediation.slnx -c Release --no-build
dotnet run --project src/CalendarRemediation.Host --launch-profile http
```

Open <http://localhost:5009>.
Stop it with `Ctrl+C`.

### Solution layout

| Project | Responsibility | References |
| --- | --- | --- |
| `src/CalendarRemediation.Core` | Platform-neutral domain foundations | None |
| `src/CalendarRemediation.Graph` | Graph discovery, mapping, and execution adapters. | Core |
| `src/CalendarRemediation.Host` | Local hosting, shared Razor shell, and CSS. | Core, Graph |
| `tests/CalendarRemediation.Core.Tests` | xUnit project for Core tests. | Core |
| `tests/CalendarRemediation.Graph.Tests` | xUnit project for Graph adapter and contract tests. | Graph, Testing |
| `tests/CalendarRemediation.Host.Tests` | xUnit project for Host tests. | Host |
| `tests/CalendarRemediation.Testing` | Shared test fixtures and Graph HTTP test handler. | Core, Graph |

Application behavior is written in C# and Razor with CSS for presentation.
Blazor framework scripts and reconnect assets come from the generated .NET template.

### Publish

Publish a complete self-contained directory for the target runtime.

```sh
dotnet publish src/CalendarRemediation.Host -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
dotnet build tools/SmokeCheck -c Release
dotnet run --project tools/SmokeCheck -c Release --no-build --no-restore -- artifacts/publish/win-x64
```

Replace both occurrences of `win-x64` with `linux-x64` to publish and check on Linux.

### Continuous integration

The [CI workflow](.github/workflows/ci.yml) runs workflows for Windows and Linux.

When pushed to `main`, it restores, verifies formatting, builds with analyzers, runs tests, and publishes the release.

## License

No license yet.
