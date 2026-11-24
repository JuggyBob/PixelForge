# Changelog

All notable changes to PixelForge are documented here. The project follows Semantic Versioning for public releases.

## [2.0.0] - 2026-11-24

First public feature release since 1.0.0. This release is published **unsigned**
(see [CODE_SIGNING.md](CODE_SIGNING.md)); the SignPath Foundation workflow in this
repository is retained for a future signed release. Windows x64 only; ARM64 is
deferred.

### Compatibility

- History migrates from schema v1 to v2 before the first read. Once migrated, a 1.0.0 build is not expected to read the profile.
- The advanced-OCR runtime moves to `llama-cpp-v1.0.0-vulkan`; older runtimes are removed only after the replacement is downloaded and verified.
- The per-user MSI refuses to install alongside an existing Inno Setup product. The Inno Setup EXE and portable ZIP remain supported.
- Windows 10 version 1809 (build 17763) or later is required; Windows 11 is recommended.

### Added

- Visual sequential Pipelines workspace under Converter, with a pannable/zoomable node canvas, snapping, drag-only action insertion at the exact drop position, live connections, expandable per-node settings, action search/categories, undo/redo, autosave, templates, batch execution through the existing queue, per-file failure isolation, cancellation, safe output naming and collision policies, intermediate cleanup, and full localization. A dedicated Upscaling action category reuses the five traditional resampling algorithms.
- Complete runtime localization for English, German, Spanish, Portuguese (Brazil), Korean, Italian, French, Japanese, Simplified Chinese, and Russian, with a flag-backed language selector in Settings, native language labels, immediate UI/dialog/notification refresh, English per-key fallback, CLDR plural forms, and locale-aware user-facing formatting. Technical identifiers, paths, codecs, engine names, and exception details remain invariant.
- Additional per-user MSI installer built with WiX, alongside the Inno Setup EXE and portable ZIP. The MSI validates the payload, protects user data on uninstall, and refuses to install over an existing Inno installation.
- Headless `pixelforge` CLI covering conversion, image/video upscaling, OCR, and media tools through the same composition root, validators, queue, engines, and history as the desktop app. See [docs/CLI.md](docs/CLI.md).
- Windows Explorer submenu and folder command that activate the running app and stage selected files on the correct page, with single-instance named-pipe handoff. See [docs/EXPLORER_INTEGRATION.md](docs/EXPLORER_INTEGRATION.md).
- Optional GitHub release update checks in Settings > About, disabled by default, with Semantic Versioning comparison, ETag caching, safe Markdown release notes, and no automatic installation. See [docs/UPDATES.md](docs/UPDATES.md).
- First-run onboarding with animated cards, spotlight highlighting, Back/Next controls, and Skip; completion is stored in the profile.
- Automatic llama.cpp runtime upgrade that downloads and verifies the current package in the background and removes the superseded runtime directory.
- Inline Watch Folder rule editing with Save/Cancel, ignored-file persistence, image/video upscaling operations, missing-model visibility, and safer active-rule reconfiguration. See [docs/WATCH_FOLDERS.md](docs/WATCH_FOLDERS.md).
- Optional motion-adaptive temporal stability for video upscaling.
- Resumable model/runtime downloads with Range requests, validators, partial-file metadata, and safe restart behavior.
- Requested/actual provider, model, runtime, fallback, warning, and engine-log fields for job items and history. See [docs/PROVIDER_TRANSPARENCY.md](docs/PROVIDER_TRANSPARENCY.md).
- Copyable, viewable, and savable diagnostic job reports with per-item batch outcomes. See [docs/JOB_REPORTS.md](docs/JOB_REPORTS.md).
- Optional OCR text review before DOCX export, transactional model installation that keeps the previous model recoverable, and versioned history JSON export/import with validation and duplicate skipping. See [docs/HISTORY_IMPORT_EXPORT.md](docs/HISTORY_IMPORT_EXPORT.md).
- Shared Converter staging session: all six categories stay staged while switching tabs or leaving the page, and "Add all to queue" validates and queues every category with its own settings.
- Category-aware folder scanning in every Converter category, including recursion, output-folder exclusion, duplicate handling, and ignored-file reporting.
- Dedicated Video Upscaler page with streaming frame-by-frame super-resolution through Real-ESRGAN ONNX models and RIFE 4.25 frame interpolation at 2x, 3x, or 4x. The stable model ID retains the historical `rife-4.25-lite` suffix; the display name was corrected to RIFE 4.25.
- Real-ESRGAN AnimeVideo v3 downloadable model for animation-oriented image and frame-by-frame video work.
- Notification center with an unread badge, live in-progress job progress bars, recent activity, per-notification removal and Cancel actions, and active download controls.
- OCR Documents workspace with an ordered page list, column-aware reading order, PP-OCRv6 Tiny/Small/Medium models, optional advanced OCR models, one combined DOCX per job, an optional PDF companion, and a review step.
- Clickable accent swatch with a generated colour wheel and synchronized RGB/HEX fields, per-format output folders in Converter settings, per-item queue cancel/remove, progressive Image Upscaler batch import, and dimension-verification tests for all five traditional resampling methods.
- Sidebar manual resizing with saved width and 240-400 DIP clamping, plus a continuous animated active-navigation indicator.

### Changed

- Localization catalog loading is exact-key-first; dynamic string pattern compilation is deferred until after first paint.
- OCR uses PP-OCRv6 DB detection plus CTC recognition through ONNX Runtime 1.24.4 DirectML or CPU. All three tiers were inference-tested on both providers with real English/Italian pages.
- OCR page resizing requests exact model dimensions, CTC decoding resets the previous character to blank, and column-aware reading order reconstructs headings, lists, and simple tables.
- Automatic DirectML image-upscaling tile size changed from 128 px to 512 px after the reference benchmark (128 px 29.4 s, 256 px 18.5 s, 512 px 15.7 s, 640 px 15.5 s, 768 px 69.1 s, 1024 px 30.1 s; 512 px with two-item overlap 16.3 s versus 21.5 s serialized). The final run reproduced 14.768 s at the 512 px default.
- The "RIFE 4.25 Lite" display name was corrected to "RIFE 4.25": the asset's ~5.67M parameters match the full network. The stable ID `rife-4.25-lite` remains for compatibility.
- Image, video, and interpolation model selectors use explicit task/scale compatibility metadata instead of display-name parsing.
- Repeated navigation to cached workspaces now completes in roughly 8-12 ms instead of 50-120 ms on the reference system; diagnostics are built lazily and capability initialization is coalesced.
- The bundled FFmpeg/FFprobe payload was reduced from about 148 MB to 63 MB with a custom LGPL build retaining all exposed codecs, containers, and filters; installed size drops by roughly 85 MB.
- The pipeline canvas is camera-based with pan, cursor-centred zoom, adaptive grid, viewport culling, on-demand connection updates, execution-order auto layout, and a single-row editor header.
- Queue and History toolbars share a 34-DIP control height; file-action headers use responsive layouts; Watch Folder rows keep actions right-aligned; the sidebar status card animates with the navigation pane.

### Fixed

- Settings no longer loses localized labels, shows an abnormal busy cursor while opening dropdowns, or crashes after navigating away and back; ten-locale switching, dropdown, and navigation regression tests pass.
- The main window enforces a 1280-DIP minimum width, so Converter tabs and right-hand action panels no longer clip when narrowed.
- Vulkan (NCNN) availability is reported correctly when the capability probe runs before the runtime manager finishes loading.
- Advanced OCR models no longer inherit PP-OCRv6-specific language text; the page reports the selected model's languages and page-level mode.
- Converter files added under different categories are no longer lost when changing pages, and queueing from any page uses the correct category settings.
- About > Open notices is idempotent and reuses the packaged notices document; OCR output no longer emits a literal `##` after a bullet list; notification badge and icon no longer intercept pointer input.
- Explorer activation strips the `--shell` sentinel, buffers cold-start requests, re-subscribes after navigation, stages folder selections on the scanner, and registers menus per file extension including HEIC/HEIF.
- The update checker no longer treats a valid older public release as unparsable; rate limits, server errors, missing releases, invalid metadata, and unsafe asset URLs get distinct states.
- Model downloads preserve partial data when paused or cancelled, verify SHA-256 before installation, and discard partials only on Cancel.
- Archive extraction rejects symbolic links and reparse points; activation-pipe messages are capped at 128K characters and scoped to the current user SID; dialogs are serialized through a single gate.
- The FFmpeg locator no longer falls back to an arbitrary `ffmpeg.exe` on `PATH`; only the bundled, validated binary is used.
- The CLI derives default audio codec/encoder from the output format, packages directories into one archive by default, exposes `--flatten`, reports missing inputs and unknown commands as exit code `2`, and provides category-scoped help.
- Dropping a file on Home switches Converter to the matching category; MP3-to-MPEG-4 audio jobs enter Running during preparation and expose cancellation.
- Pipeline node dragging uses a stable coordinate frame; deletion removes only incident connections; cycles are rejected; expansion shifts only overlapping cards; the initial Fit to View completes before the graph is revealed; clicking an action card never inserts a node.

### Notes

- Video super-resolution runs frame by frame through image models. Temporal VSR candidates (DOVE, RVRT, VRT, RealBasicVSR) were evaluated but have no verified ONNX/DirectML path.
- SwinIR and SPAN use fixed 64x64 and 256x256 tile ONNX paths; SCUNet is CPU-only after a verified native DirectML crash; NAFNet runs on DirectML/CPU.
- RIFE 4.26 and RIFE 4.26 Lite are not implemented; RIFE 4.25 remains supported. HAT, SUPIR, VOSR 2.0, and VARestorer remain excluded for memory, runtime, or license reasons. Upscayl's "Digital Art" is the existing Anime 6B weights, not a separate model.
- All three PP-OCRv6 tiers were inference-tested on DirectML and CPU. The final eight-page mixed benchmark produced 96 text lines per run: Tiny DirectML 9.311/8.301 s and CPU 8.723/8.510 s; Small DirectML 15.542/9.821 s and CPU 10.816/12.707 s; Medium DirectML 15.747/10.369 s and CPU 18.459/18.530 s.
- The final OCR harness run produced one DOCX and one PDF from three pages (39 text lines) with a real three-row Word table, two page breaks, correct page order, correct two-column order, and no literal `##` markers.
- Verification for this release: **84 unit**, **40 integration**, and **20 desktop UI** tests passed (144 total). The final Release publish payload contained **690 files**, and the final build was 2.0.0.
- This release is unsigned, including all three published assets. Windows SmartScreen may warn; verify `SHA256SUMS.txt` before running an installer.

### Packaging

- Three public assets: `PixelForge-Setup-2.0.0-x64.exe` (Inno Setup), `PixelForge-Setup-2.0.0-x64.msi` (WiX), and `PixelForge-Portable-2.0.0-x64.zip`, with SHA-256 checksums in `SHA256SUMS.txt`.
- Self-contained Windows x64 payload with bundled FFmpeg/FFprobe, Pandoc, Typst, 7-Zip, Zstandard, ImageMagick, ONNX Runtime/DirectML, RNNoise, and u2netp. AI model weights and NCNN/llama.cpp runtimes are downloaded on demand and verified before use.
- Existing v1.0.0 assets are unchanged and unsigned.

## [1.0.0] - 2026-11-14

First public release.

### Added

- Unified Converter workspace for images, audio, video, documents, e-books, and archives.
- Image conversion and processing through ImageMagick/Magick.NET, including animation, multipage, alpha, ICC, EXIF orientation, and bounded-memory large-image handling.
- Audio and video conversion through FFmpeg, including stream copy/remux, codec-aware quality controls, hardware encoder probing, subtitles, metadata, and output verification.
- Document and e-book conversion through Pandoc and Typst; archive creation, repackaging, safe extraction, and Zstandard compression.
- Media Tools for loudness normalisation, RNNoise/spectral denoising, silence removal, trim/merge, channel and sample-rate conversion, metadata, image optimisation, background removal, batch editing, watermarking, video compression, stabilisation, thumbnails, GIFs, subtitle handling, HDR-to-SDR, and resolution/frame-rate conversion.
- Image Upscaler with traditional resampling, ONNX models, and NCNN/Vulkan models, tiled inference, CPU/DirectML/Vulkan providers, and a synchronized before/after inspector.
- Video Upscaler with frame-by-frame AI super-resolution and RIFE 4.25 interpolation, scene-cut handling, audio preservation, and FFprobe verification.
- OCR Documents with PP-OCRv6 Tiny/Small/Medium and optional GLM-OCR, MiniCPM-OCR 1.0, and Xiaomi-OCR-0 through llama.cpp; ordered DOCX and optional PDF output.
- Shared AI Models panel with catalogue downloads, install-from-file, SHA-256 verification, removal, and runtime management.
- Persistent queue with configurable concurrency, cancellation, retry, recovery, progress, and notifications.
- Watch Folders with stability checks, output-loop protection, processed-file state, and queue integration.
- SQLite-backed History with search, filters, repeat, compare, retention, and clear actions.
- WinUI 3 shell with theme modes, custom accent color, bundled JetBrains Mono, font scaling, collapsible/resizable sidebar, accessible automation names, and a notification centre.

### Packaging

- Self-contained Windows x64 installer and portable ZIP.
- Bundled FFmpeg/FFprobe, Pandoc, Typst, 7-Zip, Zstandard, ImageMagick, ONNX Runtime/DirectML, RNNoise, and u2netp.
- Optional AI model weights and NCNN/llama.cpp runtimes downloaded on demand and verified before use.
- SHA-256 checksums for release assets.

### Known limitations

- Windows x64 only.
- Optional AI models are large downloads and require compatible runtimes.
- Advanced OCR and AI upscaling depend on available GPU/runtime support.
- Installer is not code-signed.
