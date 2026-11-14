# Architecture

PixelForge is a layered .NET 10 WinUI 3 desktop application. The UI never embeds a browser or a web server; engines are invoked locally through typed adapters.

## Projects

| Project | Responsibility |
|---|---|
| `PixelForge.Domain` | Models, interfaces, capability catalogues, settings contracts |
| `PixelForge.Application` | Job orchestration, capability probing, model management, media inspection |
| `PixelForge.Infrastructure` | SQLite persistence, settings, logging, paths, process runner, diagnostics |
| `PixelForge.Engines.*` | FFmpeg, ImageMagick, documents, archives, image tools, upscaling, OCR |
| `PixelForge.Composition` | Dependency injection and service registration |
| `PixelForge.Presentation.WinUI` | WinUI 3 views, view models, navigation, shell |
| `PixelForge.Tests.*` | Unit, integration, and UI verification |

## Execution flow

1. The WinUI shell loads settings and starts the queue service on the UI dispatcher.
2. Engine probes report actual capabilities; the registry caches a verified snapshot and refreshes it in the background.
3. A page prepares a `JobRequest`; validation checks formats, codecs, providers, output paths, and model availability.
4. The queue persists the job, dispatches it to a bounded worker pool, and reports progress through events.
5. Engines run as external processes or local inference sessions. Outputs are verified before being committed.
6. History, notifications, and the UI observe the queue without owning output files.

## External engines

| Engine | Integration |
|---|---|
| FFmpeg/FFprobe | Separate process, argument-list invocation, stdout/stderr parsing, cancellation kills the process tree |
| Pandoc/Typst | Separate processes; Pandoc produces documents, Typst produces PDF |
| 7-Zip/Zstandard | Separate processes with safe extraction validation |
| ImageMagick | Magick.NET native library |
| ONNX Runtime/DirectML | In-process inference sessions; CPU fallback |
| NCNN/Vulkan | Downloaded runtime invoked as a separate process |
| llama.cpp | Downloaded local server bound to loopback for advanced OCR |

## Data

Settings, queue state, history, logs, caches, and downloaded models live under `%LOCALAPPDATA%\PixelForge` by default. `PIXELFORGE_DATA_DIR` overrides the root for isolated profiles. The application never stores copies of user media in history.

## Safety boundaries

- Archive extraction rejects absolute paths, traversal, oversized entry counts, and expanded-size abuse before committing output.
- Model downloads and runtime packages are size and SHA-256 verified before use.
- Hardware encoders are advertised only after a short real encode probe succeeds.
- Outputs are verified before replacing a previous file; a failed job does not delete a committed output.
