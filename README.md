# Calendar Remediation

A workspace for reviewing calendar scheduling against organizer intent, built with the .NET.

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
| `tests/CalendarRemediation.Tests` | xUnit project for domain and contract tests. | Core, Graph, Host |

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
