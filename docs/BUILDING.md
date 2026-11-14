# Building PixelForge

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Windows | 10 1809+ | Windows 11 recommended |
| .NET SDK | 10.0.404+ | Pinned by `global.json` |
| Windows SDK | 10.0.26100 | WinUI/Windows App SDK build targets |
| Visual Studio Build Tools | 2022/2026 with C++ workload | Required for native Windows App SDK dependencies |
| PowerShell | 7.x | Build and dependency scripts |
| Inno Setup | 7.x | Optional; required only for the installer |

## Restore dependencies

```powershell
dotnet restore PixelForge.sln
```

The repository excludes large redistributable engines from version control. Fetch the build-time dependencies with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-dependencies.ps1 -SkipModels
```

This downloads and verifies:

- FFmpeg/FFprobe (LGPL shared build)
- Pandoc
- Typst
- JetBrains Mono
- Small image fixtures when models are requested

Small components required for a normal build are checked in: 7-Zip, Zstandard, RNNoise, u2netp, PP-OCRv6 dictionaries, licenses, and the JetBrains Mono font. Optional AI models remain opt-in; do not commit downloaded weights.

## Build

```powershell
dotnet build PixelForge.sln -c Release -p:Platform=x64
```

The application output is:

```text
src\PixelForge.Presentation.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\PixelForge.exe
```

## Tests

```powershell
dotnet test tests\PixelForge.Tests.Unit\PixelForge.Tests.Unit.csproj -c Release --no-build
dotnet test tests\PixelForge.Tests.Integration\PixelForge.Tests.Integration.csproj -c Release --no-build
```

UI tests require a desktop session, external engines, and the optional model fixtures. They run sequentially because they drive a single WinUI window.

## Release packages

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.0.0
```

This produces:

```text
artifacts\installer\PixelForge-1.0.0-Setup.exe
artifacts\portable\PixelForge-1.0.0-portable.zip
artifacts\SHA256SUMS.txt
```

The installer is per user and does not require administrator rights. Verify it without touching an existing installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-installer.ps1 `
  -Installer artifacts\installer\PixelForge-1.0.0-Setup.exe
```

### Staged builds for signing

The signed-release workflow builds and signs in two stages so the installer contains the signed application executable.

Publish only:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.0.0 -Stage publish
```

After `PixelForge.exe` has been signed, package the signed application:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 `
  -Version 1.0.0 -Stage package -SignedAppPath <path-to-signed-PixelForge.exe>
```

The package stage verifies that the supplied executable has a valid Authenticode signature from SignPath Foundation before it creates the portable ZIP or installer. The default `-Stage all` still performs an unsigned local build.

### Verify signatures

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-signatures.ps1 `
  -Path artifacts\release\v1.0.0\PixelForge-Setup-1.0.0-x64.exe
```

The script requires a valid signature, a signer containing `SignPath Foundation`, and a timestamp countersignature.

### Signed release workflow

[`.github/workflows/release-sign.yml`](../.github/workflows/release-sign.yml) runs on a `v*` tag or manual dispatch. It requires the SignPath secret and variables described in [SIGNPATH.md](SIGNPATH.md), waits for manual approval in SignPath, verifies the signed application and installer, generates checksums after signing, and creates a draft GitHub release. Do not replace the existing unsigned `v1.0.0` assets; use a new version.

## Repository layout

```text
src/                       Application, domain, infrastructure, engines
tests/                     Unit, integration, and UI test projects
tools/                     Harness and UI automation utilities
packaging/                 Inno Setup definition
scripts/                   Build, dependency, and verification scripts
testdata/fixtures/         Synthetic media fixtures for tests
docs/                      Public documentation and screenshots
licenses/                  Third-party license texts
third_party/               Small checked-in assets and license notices
```

Do not commit build output, generated installers, downloaded models, personal settings, or test evidence.
