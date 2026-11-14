# Changelog

All notable changes to PixelForge are documented here. The project follows Semantic Versioning for public releases.

## [Unreleased]

### Added

- SignPath Foundation code-signing workflow for two-stage Authenticode signing: the application executable is signed first, then the installer is rebuilt around it and signed, with checksums generated after signing.
- Artifact configurations with enforced product name, product version, and file version metadata.
- Code signing policy and privacy policy documentation.

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
