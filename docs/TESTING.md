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
```

Run integration/UI tests only after fetching the external engines and, for AI tests, installing the verified optional models. The UI tests use an isolated `PIXELFORGE_DATA_DIR` and delete their temporary profile by default.

## Release verification performed for 1.0.0

- Release build and self-contained publish.
- Installer install, dependency checks, launch, uninstall, and shortcut cleanup.
- Portable extraction, dependency checks, and launch.
- FFmpeg, Pandoc, Typst, 7-Zip, Zstandard, RNNoise, u2netp, ImageMagick, and ONNX Runtime presence checks.
- UI capture of all ten destinations and the shared model panel from an isolated profile.
- Unit and integration suites.
- Screenshot privacy review for visible filenames, paths, and personal data.

Hardware-dependent features are verified only when the required GPU, driver, and optional model are available. The application distinguishes unavailable providers from tested ones; it does not claim universal hardware acceleration.
