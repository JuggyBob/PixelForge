# Contributing to PixelForge

Thank you for helping improve PixelForge. By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- Search existing issues and pull requests first.
- For substantial features or architectural changes, open a feature request before writing the implementation.
- Report security issues privately through [SECURITY.md](SECURITY.md), not in a public issue.
- Do not include personal media, credentials, private paths, or model weights in commits, tests, screenshots, or logs.

## Development setup

1. Install Windows 10 1809+ (Windows 11 recommended), .NET SDK 10.0.404+, Windows SDK 10.0.26100, and Visual Studio Build Tools with the Windows application development/C++ workload.
2. Restore dependencies:
   ```powershell
   dotnet restore PixelForge.sln
   ```
3. Fetch build-time engines:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-dependencies.ps1 -SkipModels
   ```
4. Build:
   ```powershell
   dotnet build PixelForge.sln -c Release -p:Platform=x64
   ```

See [docs/BUILDING.md](docs/BUILDING.md) for packaging and test details.

## Code organization

- `PixelForge.Domain` — models, interfaces, catalogues.
- `PixelForge.Application` — orchestration, validation, capabilities, models.
- `PixelForge.Infrastructure` — persistence, settings, logging, diagnostics.
- `PixelForge.Engines.*` — media and document engines.
- `PixelForge.Presentation.WinUI` — WinUI views, view models, shell.
- `tests/` — unit, integration, and UI tests.

Match the existing C# conventions: nullable enabled, file-scoped namespaces, `sealed` classes where appropriate, `async`/`await` for I/O, and typed argument lists for external processes (never shell string concatenation).

## Branches and commits

Use short-lived branches:

- `feature/<description>`
- `fix/<description>`
- `docs/<description>`
- `test/<description>`

Commit messages should be imperative and scoped, for example:

```text
converter: preserve animated WebP frames
docs: document optional model downloads
```

Keep commits focused. Do not commit build output, downloaded models, test evidence, personal settings, signing keys, or generated installers.

## Tests

Before opening a pull request:

```powershell
dotnet test tests\PixelForge.Tests.Unit\PixelForge.Tests.Unit.csproj -c Release
```

Run integration or UI tests when your change touches engines, navigation, settings, or media behavior. If a test requires optional models or hardware, state exactly what was and was not verified.

Do not weaken or skip a test just to make a failing change pass. Fix the cause or report the limitation.

## Pull requests

Include:

- A concise summary and motivation.
- Screenshots for visible UI changes, captured from the real application with neutral demo content.
- Test commands and results.
- Documentation updates where behavior, formats, requirements, or limitations changed.
- Confirmation that no secrets, personal data, or generated binaries were added.

Pull requests are reviewed for correctness, safety, licensing, test coverage, and consistency with the application's local-first design. Do not add telemetry, hidden network calls, or bundled model weights without prior discussion.

## Bug reports

Use the bug report template. Include the PixelForge version, Windows version, relevant hardware/runtime details, reproduction steps, expected and actual behavior, and logs with personal information removed.

## Feature requests

Describe the problem and the desired outcome. If the feature depends on a new runtime, model, or external engine, include its source, license, size, and redistribution terms.

## Licensing

By contributing, you agree that your contribution may be distributed under the Apache License 2.0. Do not copy code or assets with incompatible terms. Add or update third-party notices when introducing a dependency.
