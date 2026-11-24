# Testing

## Automated suites

| Suite | Purpose |
|---|---|
| `PixelForge.Tests.Unit` | Domain, settings, capability, and validation tests; no external engines required |
| `PixelForge.Tests.Integration` | Real FFmpeg, document, archive, image, and media-operation tests against synthetic fixtures |
| `PixelForge.Tests.Ui` | WinUI navigation, settings persistence, conversion, queue, OCR, and screenshot capture in a real desktop session |

Run the unit suite on any Windows build machine:

```powershell
dotnet test tests\PixelForge.Tests.Unit\PixelForge.Tests.Unit.csproj -c Release --no-build
dotnet test tests\PixelForge.Tests.Integration\PixelForge.Tests.Integration.csproj -c Release --no-build
dotnet test tests\PixelForge.Tests.Ui\PixelForge.Tests.Ui.csproj -c Release --no-build
```

Run integration/UI tests only after fetching the external engines and, for AI tests, installing the verified optional models. The UI tests use an isolated `PIXELFORGE_DATA_DIR` and delete their temporary profile by default, and require an interactive desktop session.

## Release verification performed for 2.0.0

- Final build: **PixelForge 2.0.0**. The final Release publish payload contained **690 files**.
- Automated suites: **84 unit**, **40 integration**, and **20 desktop UI** tests passed — **144 passed, 0 failed**.
- Desktop UI coverage: real WinUI launch, theme and language persistence, file-picker import, Add to queue and output verification, History, navigation responsiveness, notification center, Explorer `--shell` cold-start staging, first-run onboarding, Watch rule alignment and editor reopen, runtime language switching, startup locale rendering, and Pipelines click-vs-drag behavior with in-app pointer insertion at the exact release position, autosave, runtime Spanish switching, and preservation of unavailable action IDs.
- Integration coverage: real image conversions, all five traditional resampling methods with dimensions verified, audio/video conversion and remuxing, SQLite history and schema migration, document/archive workflows, settings persistence, folder scanning, update-checker outcomes, and real multi-step pipeline execution including failure isolation and cancellation.
- Headless harness checks covered the image/audio/video matrices, tools, OCR and OCR benchmarks, NCNN/Vulkan, fixed-tile ONNX, restoration, advanced OCR, video upscaling and cancellation, watch folders, and a soak run.
- Installer verification covered the per-user Inno Setup EXE, the per-user MSI, and the portable ZIP: install, launch, CLI conversion, uninstall, and user-data preservation.
- The v2.0.0 assets are **unsigned**, including the MSI. SHA-256 checksums are generated from the final assets and published in `SHA256SUMS.txt`; Windows SmartScreen may show a warning.

## Release verification performed for 1.0.0 (historical)

- Release build and self-contained publish.
- Installer install, dependency checks, launch, uninstall, and shortcut cleanup.
- Portable extraction, dependency checks, and launch.
- FFmpeg, Pandoc, Typst, 7-Zip, Zstandard, RNNoise, u2netp, ImageMagick, and ONNX Runtime presence checks.
- UI capture of all ten destinations and the shared model panel from an isolated profile.
- Unit and integration suites.
- Screenshot privacy review for visible filenames, paths, and personal data.

Hardware-dependent features are verified only when the required GPU, driver, and optional model are available. The application distinguishes unavailable providers from tested ones; it does not claim universal hardware acceleration.
