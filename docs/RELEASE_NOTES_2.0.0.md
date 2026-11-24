# PixelForge 2.0.0

PixelForge is a local Windows studio for converting, processing, upscaling, and
OCR-ing media. It runs on your PC; no account or cloud upload is required.
This is the first public feature release since 1.0.0: it includes the document,
e-book, archive, media-tools, and watch-folder work, plus visual Pipelines,
ten-language localization, a headless CLI, Explorer integration, an optional
update check, and an MSI installer.

> **Unsigned release.** The v2.0.0 assets are not code-signed. Windows
> SmartScreen may show a warning. Verify the SHA-256 checksums below before
> running any installer. PixelForge 1.0.0 was also unsigned; SignPath signing
> is not yet configured. See [CODE_SIGNING.md](../CODE_SIGNING.md).

## Downloads

| Asset | Download |
|---|---|
| Inno Setup installer (per user, x64) | [PixelForge-Setup-2.0.0-x64.exe](https://github.com/JuggyBob/PixelForge/releases/download/v2.0.0/PixelForge-Setup-2.0.0-x64.exe) |
| MSI installer (per user, x64) | [PixelForge-Setup-2.0.0-x64.msi](https://github.com/JuggyBob/PixelForge/releases/download/v2.0.0/PixelForge-Setup-2.0.0-x64.msi) |
| Portable ZIP | [PixelForge-Portable-2.0.0-x64.zip](https://github.com/JuggyBob/PixelForge/releases/download/v2.0.0/PixelForge-Portable-2.0.0-x64.zip) |
| Checksums | [SHA256SUMS.txt](https://github.com/JuggyBob/PixelForge/releases/download/v2.0.0/SHA256SUMS.txt) |

Final asset sizes and SHA-256 values:

| Asset | Size | SHA-256 |
|---|---|---|
| `PixelForge-Setup-2.0.0-x64.exe` | 183.3 MB | `884c4d1e9a74133b5a3f83a377b9b4114d94037e9a0569d0dd6c145d80622a68` |
| `PixelForge-Setup-2.0.0-x64.msi` | 225.7 MB | `3c5b133f3a01cf92b9ab7ee99cf165464208541b8cf355a5b86ac0ff14c1554b` |
| `PixelForge-Portable-2.0.0-x64.zip` | 268.6 MB | `53e81ed558b8dcca44e3cd916e967ae87b45acb6efc4f19c98c2604210948c5d` |

## Highlights

- **Visual Pipelines** under Converter — chain operations on a pannable node
  canvas with snapping, drag-only action insertion at the exact drop position,
  templates, undo/redo, autosave, batch execution through the shared queue,
  per-file failure isolation, cancellation, and safe output naming. A dedicated
  Upscaling action category reuses the five traditional resampling algorithms.
  See [Pipelines](PIPELINES.md).
- **Ten-language runtime localization** — English, German, Spanish, Portuguese
  (Brazil), Korean, Italian, French, Japanese, Simplified Chinese, and Russian,
  with a Settings language selector, bundled flags, native labels, English
  per-key fallback, CLDR pluralization, and locale-aware formatting. Technical
  identifiers, paths, codecs, and engine names remain unchanged.
- **Headless `pixelforge` CLI** for conversion, image/video upscaling, OCR, and
  media tools through the same engines and queue as the desktop app. See
  [CLI](CLI.md).
- **Windows Explorer integration** — submenu and folder command that activate
  the running app and stage files on the correct page. See
  [Explorer integration](EXPLORER_INTEGRATION.md).
- **Optional GitHub update checks** in Settings > About, off by default, with no
  telemetry and no automatic installation. See [Update checking](UPDATES.md).
- **MSI installer** alongside the existing Inno Setup EXE and portable ZIP. The
  MSI installs per user and refuses to install over an existing Inno product.
- First-run onboarding, inline Watch Folder rule editing, optional temporal
  stability for video upscaling, resumable verified downloads, requested-vs-actual
  provider reporting, copyable job reports, versioned history export/import,
  automatic llama.cpp runtime upgrades, and a smaller custom LGPL FFmpeg build
  (about 63 MB, roughly 85 MB less than before).

## Added, changed, and fixed

- **Added:** shared Converter staging session across all six categories;
  category-aware folder scanning; dedicated Video Upscaler with streaming
  frame-by-frame super-resolution and RIFE 4.25 interpolation at 2x, 3x, or 4x;
  Real-ESRGAN AnimeVideo v3; OCR page review and column-aware reading order;
  PP-OCRv6 Tiny/Small/Medium with optional advanced OCR models; notification
  center with live progress, per-item Cancel, and download controls; per-format
  output folders; per-item queue cancel/remove; progressive batch import;
  clickable accent swatch and colour wheel; sidebar resizing.
- **Changed:** OCR uses PP-OCRv6 DB detection plus CTC recognition through ONNX
  Runtime 1.24.4; the automatic DirectML tile default is 512 px after the
  reference benchmark; the "RIFE 4.25 Lite" display name was corrected to
  **RIFE 4.25** (the stable ID `rife-4.25-lite` remains for compatibility);
  model selectors use explicit task/scale metadata; repeated navigation to
  cached workspaces takes roughly 8-12 ms; diagnostics are built lazily; the
  pipeline canvas is camera-based with pan, zoom, and viewport culling.
- **Fixed:** settings locale/navigation regressions; 1280-DIP minimum window
  width; Vulkan provider reporting; converter files lost on page change;
  literal `##` in OCR output; notification badge pointer handling; Explorer
  activation and per-extension menu registration; update checker treating a
  valid older release as unparsable; partial model downloads; archive symlink
  rejection; `PATH` FFmpeg fallback; CLI codec defaults, archive packaging,
  `--flatten`, exit codes, and category-scoped help; and multiple pipeline
  editor interaction issues.

## Requirements and installation

Windows 10 version 1809 (build 17763) or later; Windows 11 recommended. x64 only;
ARM64 is deferred. 8 GB RAM minimum, 16 GB or more for AI/video work.
DirectML-compatible GPU recommended for AI acceleration; Vulkan for
NCNN/llama.cpp; CPU fallback is available.

- **EXE:** run `PixelForge-Setup-2.0.0-x64.exe`. It installs per user to
  `%LOCALAPPDATA%\Programs\PixelForge`, needs no administrator rights, and adds
  optional desktop and Start menu shortcuts.
- **MSI:** run `PixelForge-Setup-2.0.0-x64.msi`. Per-user install to the same
  location; refuses to install while the Inno Setup product is installed.
  Windows may list the MSI product in the machine-wide Add/Remove Programs view
  while the files remain under the current user.
- **Portable:** extract `PixelForge-Portable-2.0.0-x64.zip` and run
  `PixelForge.exe`. Set `PIXELFORGE_DATA_DIR` for a beside-the-app profile.

The CLI is at `cli\pixelforge.exe` in the installed and portable layouts.
AI model weights, NCNN, and llama.cpp runtimes are downloaded only when a
selected model needs them; u2netp and RNNoise are bundled.

## Compatibility and upgrade notes

- History migrates from schema v1 to v2 before the first read. After migration,
  a 1.0.0 build is not expected to read the profile. See
  [History export and import](HISTORY_IMPORT_EXPORT.md).
- Advanced OCR moves to `llama-cpp-v1.0.0-vulkan`; older runtimes are replaced
  and removed only after verification. Existing models, settings, history, and
  watch rules are preserved.
- Keep either the EXE or the MSI installed, not both: the MSI refuses to install
  over an existing Inno Setup product. The portable ZIP remains independent.
- Existing v1.0.0 release assets are unchanged and unsigned.

## Verification

- Final build: **PixelForge 2.0.0**. Final Release publish payload: **690 files**.
- Automated suites: **84 unit**, **40 integration**, **20 desktop UI** —
  **144 passed, 0 failed**.
- Desktop UI coverage included real launch, theme and language persistence,
  file-picker import, queue/output verification, notification center, Explorer
  cold-start staging, first-run onboarding, Watch rule editing, Pipelines
  click-vs-drag insertion and autosave, and runtime language switching.
- Integration and harness checks ran real conversions for the image, audio,
  video, document, e-book, archive, OCR, upscaling, interpolation, tools, and
  watch-folder workflows, plus cancellation and soak scenarios.
- Hardware-dependent and subjective visual checks are not universal; providers
  and encoders are reported honestly and fall back to CPU where supported.

Verify your downloads:

```powershell
Get-FileHash .\PixelForge-Setup-2.0.0-x64.exe -Algorithm SHA256
Get-FileHash .\PixelForge-Setup-2.0.0-x64.msi -Algorithm SHA256
Get-FileHash .\PixelForge-Portable-2.0.0-x64.zip -Algorithm SHA256
```

Compare with `SHA256SUMS.txt`. `Get-AuthenticodeSignature` reports `NotSigned`
for these assets, which is expected for this release.

## Known limitations

- Windows x64 only; ARM64 is deferred; macOS and Linux are not supported.
- MSIX is not produced. The MSI is a per-user package, not an MSIX.
- DirectML and Vulkan availability depend on GPU and driver; CPU fallback is
  used where supported.
- On Windows 11, classic Explorer verbs appear under **Show more options**.
- Video super-resolution is frame-by-frame; fast motion can shimmer. RIFE 4.26
  is not implemented; **RIFE 4.25** remains the supported interpolator. Temporal
  VSR models and several research models are not integrated.
- OCR is page-level reconstruction, not full layout analysis; complex forms,
  seals, formulas, and handwriting may need review. PDFs must be rasterized first.
- AI models and runtimes are large optional downloads. The v2.0.0 assets,
  including the MSI, are unsigned and SmartScreen may warn.

## Code signing

Free code signing is provided by [SignPath.io](https://about.signpath.io),
certificate by [SignPath Foundation](https://signpath.org). SignPath is not
configured for this release: v2.0.0 is unsigned, as was 1.0.0. A future version
will be the first signed release after enrollment and manual approval. See
[CODE_SIGNING.md](../CODE_SIGNING.md) and [SignPath setup](SIGNPATH.md).

## Links

- [README](https://github.com/JuggyBob/PixelForge/blob/main/README.md)
- [Changelog](https://github.com/JuggyBob/PixelForge/blob/main/CHANGELOG.md)
- [Code signing policy](https://github.com/JuggyBob/PixelForge/blob/main/CODE_SIGNING.md)
- [Security policy](https://github.com/JuggyBob/PixelForge/blob/main/SECURITY.md)
- [Privacy policy](https://github.com/JuggyBob/PixelForge/blob/main/PRIVACY.md)
- [Third-party notices](https://github.com/JuggyBob/PixelForge/blob/main/THIRD_PARTY_NOTICES.md)
- [Build and test](https://github.com/JuggyBob/PixelForge/blob/main/docs/BUILDING.md)
- [Testing and verification](https://github.com/JuggyBob/PixelForge/blob/main/docs/TESTING.md)
- [Update checking](https://github.com/JuggyBob/PixelForge/blob/main/docs/UPDATES.md)
- [Release notes template](RELEASE_NOTES_TEMPLATE.md)
