# Supported Formats and Capabilities

This document describes the formats verified with the PixelForge 2.0.0 engines on the reference Windows 11 system. The converter only lists a target format when the installed engine reports that it can actually write it.

## Images

### Output formats

| Target | Extension | Lossy | Alpha | Animation / pages | Notes |
|---|---|:---:|---|---|---|
| PNG | `.png` | No | Yes | Animated / multipage where supported | Quality/compression level; preserves animation if requested |
| JPEG | `.jpg` | Yes | No | No | Quality, progressive mode, background colour for transparency, EXIF orientation normalized |
| WebP | `.webp` | Optional | Yes | Animated | Quality or lossless mode |
| AVIF | `.avif` | Yes | Yes | Animated | Encoder speed 0–10; slower presets can be significantly slower |
| TIFF | `.tif` | Optional | Yes | Multipage | Compression choice (LZW default); preserve pages |
| BMP | `.bmp` | No | Yes | No | Uncompressed-compatible bitmap output |
| GIF | `.gif` | Yes | Yes | Animated | Palette size 2–256; animation preserved unless flattened |
| ICO | `.ico` | No | Yes | Multi-size icon | Sizes list, default `256,128,64,48,32,16` |
| JPEG XL | `.jxl` | Optional | Yes | Animated where supported | ImageMagick-dependent encoder |
| TGA | `.tga` | No | Yes | No | Truevision TGA |
| PNM | `.pnm` | No | No | No | Portable anymap |
| OpenEXR | `.exr` | Optional | Yes | No | Floating-point-capable format |
| Radiance HDR | `.hdr` | Optional | No | No | Basic Radiance output |
| QOI | `.qoi` | No | Yes | No | Quite OK Image |
| DDS | `.dds` | Optional | Yes | No | DirectDraw surface |

### Input-only image formats

| Input | Notes |
|---|---|
| HEIC / HEIF | Read and inspected; not offered as an output target. Decode support is provided by the bundled ImageMagick build. |
| SVG / SVGZ | Read as vector input. The converter requires an explicit raster resolution (resize width or height). SVG is not an output target. |
| PSD / PSB | Read as flattened or multipage input depending on the file. PSD is not an output target. |
| Camera raw (`.dng`, `.cr2`, `.nef`, `.arw`, etc.) | Accepted by the input filter when supported by the installed ImageMagick build; the file is content-validated before being queued. |
| Other ImageMagick-readable inputs | PCX, XCF, and other formats may appear as readable inputs if the engine supports them. |

### Image behavior and limits

- **Animated and multipage images**: preservation depends on the target. JPEG/BMP cannot represent animation; enabling flattening produces a single frame. ICO and TIFF use page/size lists rather than time-based animation.
- **Alpha**: preserved where the target supports it; otherwise flattened onto the configured background colour.
- **EXIF orientation**: normalized during conversion. The output pixels are oriented as displayed.
- **ICC profiles**: preserved when the target and source allow it.
- **Large images**: tested at 6000 × 4000 and with high-resolution resizing. ImageMagick resource limits are configured to keep memory bounded.
- **SVG**: vector input must be rasterized by the image converter before it can be used in the upscaler.
- **Upscaler input**: SVG, animated, multipage, and unreadable images are deliberately skipped with a reason; the upscaler does not silently flatten them.
- **Downloadable image models**: Real-ESRGAN x4plus, x2plus, x4plus Anime 6B, General x4 v3, and AnimeVideo v3. The AnimeVideo v3 model is specialized for animation and is also offered for frame-by-frame video work; it is not a temporal video model.

## Audio

### Containers and codecs

| Container | Extension | Codecs available in the bundled build |
|---|---|---|
| MP3 | `.mp3` | MP3 (`libmp3lame`) |
| WAV | `.wav` | PCM signed 16-bit little-endian |
| FLAC | `.flac` | FLAC (compression level 0–12) |
| M4A | `.m4a` | AAC, ALAC |
| AAC | `.aac` | AAC |
| OGG | `.ogg` | Opus, Vorbis |
| Opus | `.opus` | Opus |

### Audio settings

- Constant bitrate or variable quality where the encoder supports it.
- Bitrate presets from 64 to 320 kbps for lossy encoders.
- Sample rate and channel layout conversion (source, mono, stereo, 5.1 where supported).
- FLAC compression level.
- Metadata preservation.

Input detection uses FFprobe, so audio files with unexpected extensions are still identified by their contents. Additional read formats such as WMA, AIFF, AC-3, DTS, AMR, and MKA are accepted when the bundled FFmpeg can read them.

## Video

### Containers and compatible codecs

The following matrix is produced from the actual capability probe of the bundled FFmpeg build:

| Container | Video codecs | Audio codecs |
|---|---|---|
| MP4 | AV1, H.264, H.265/HEVC, MPEG-4 Part 2 | AAC, MP3 |
| MKV | Apple ProRes, AV1, H.264, H.265/HEVC, MPEG-4, VP8, VP9 | AAC, ALAC, FLAC, MP3, Opus, PCM, Vorbis |
| MOV | Apple ProRes, H.264, H.265/HEVC, MPEG-4 | AAC, ALAC, MP3, PCM |
| WebM | AV1, VP8, VP9 | Opus, Vorbis |
| AVI | MPEG-4 Part 2 | MP3, PCM |
| Animated GIF | GIF | None |

### Encoder availability

| Codec | Software encoder | Hardware encoders on the reference system |
|---|---|---|
| H.264 | `libopenh264` (BSD); Windows Media Foundation `h264_mf` when available | AMD AMF `h264_amf` (verified) |
| H.265/HEVC | Kvazaar `libkvazaar` | AMD AMF `hevc_amf` (verified) |
| AV1 | SVT-AV1 `libsvtav1`, libaom `libaom-av1` | AMD AMF `av1_amf` (verified) |
| VP9 | `libvpx-vp9` | — |
| VP8 | `libvpx` | — |
| MPEG-4 Part 2 | `mpeg4` | — |
| Apple ProRes | `prores_ks` | — |
| Animated GIF | `gif` | — |

ProRes quality is selected with a profile preset (`proxy`, `lt`, `standard`, `hq`, `4444`, `4444xq`) rather than bitrate or CRF.

Hardware encoders are exposed only when a short real encode probe succeeds. On other systems, available hardware encoders depend on the GPU, driver, and FFmpeg build support. DirectML is used for AI upscaling and u2netp background removal independently of the FFmpeg video encoder path.

## Watch folders

Watch folders monitor a chosen input folder (optionally recursively), wait until a file is stable, inspect it, validate the operation, and enqueue a normal central-queue job. Rules and processed-file signatures are persisted so restart does not reprocess the same unchanged file. Output folders are ignored to prevent loops, and failed files are recorded for review rather than retried endlessly. Source files are never deleted.

### Video modes

- **Transcode** — decode and re-encode to the selected codec/encoder.
- **Stream copy / remux** — copy video and/or audio without re-encoding where the target container supports it. Useful for changing MP4 ↔ MKV/MOV without quality loss.
- **Quality or bitrate rate control** — CRF/QP/CQ or target bitrate, depending on the encoder.
- **Encoder presets** — encoder-specific preset list, selected only when supported.
- **Resize** — fit, crop, or stretch with aspect-ratio control; dimensions are rounded to encoder requirements.
- **Frame rate** — source or explicit output rate.
- **Subtitles** — copy, convert, or drop, depending on container support.
- **Chapters and metadata** — preserved when selected and supported.
- **Fast start** — web-friendly MP4 layout.
- **Output verification** — FFprobe checks streams, codecs, dimensions, duration, and output integrity after conversion.

## Video Upscaler

The dedicated Video Upscaler is a separate operation from ordinary video conversion.

| Input | Output | Notes |
|---|---|---|
| Video formats accepted by the bundled FFmpeg build | MP4 with H.264 video | Audio is copied when the source codec is MP4-compatible (AAC, MP3, AC-3, E-AC-3, ALAC); otherwise it is re-encoded to AAC |
| Video with no audio | MP4 with H.264 video, silent | Verified as intentionally silent |
| Video with subtitles or chapters | MP4 with H.264 video | Subtitles and chapters are not preserved; the warning is shown before queueing |
| Video with rotation metadata | MP4 with H.264 video | Rotation is applied to the output frames; the MP4 itself has no rotation flag |

Frame-by-frame super-resolution uses dynamic ONNX image models from the Image Upscaler. NCNN-only Waifu2x/Real-CUGAN and fixed-tile SwinIR/SPAN models are intentionally excluded from the video pipeline, and 1× restoration models are not video upscalers. Only the model's native scale is offered, so the result is a genuine AI upscale rather than a hidden conventional resize. Frame interpolation uses RIFE 4.25 and supports 2×, 3×, and 4× multipliers. The stable model ID remains `rife-4.25-lite` for settings/download compatibility, but the asset is displayed as RIFE 4.25 because its parameter count matches the full network. Scene cuts are detected and are not interpolated across. The final resolution, frame rate, duration, audio presence, and readability are verified with FFprobe before the temporary output is committed.

## OCR Documents

The OCR workspace accepts page images and produces one ordered document per job through the central queue.

| Input | Output | Notes |
|---|---|---|
| PNG, JPEG, WebP, BMP, TIFF and other static raster images supported by the image engine | One DOCX per job | Primary output; page order is the order shown in the workspace |
| The same image inputs | Optional PDF companion | Generated from the DOCX, so content and page order match |
| Clipboard image or image files copied in File Explorer | Same as above | `Ctrl+V` pastes into the page list |
| A folder of images | Same as above | Recursive scanning, natural/created/modified/EXIF/path sorting, output-folder exclusion |

Animated, multipage, and vector images are rejected because one page image must represent one document page. PDF files are not accepted directly; rasterize a scanned PDF first. OCR reconstructs text in column-aware reading order, plus headings, paragraphs, lists, simple tables, and page breaks; dense columns, forms, seals, low-resolution handwriting, and decorative layouts may still need review. Low-confidence pages are reported as warnings. No translation is performed.

| OCR model | Backend | Tier | Files | Total size | SHA-256 |
|---|---|---|---:|---:|---|
| PP-OCRv6 Tiny | ONNX DirectML/CPU | 1.5M parameters | Detector + recognizer + dictionary | 6.2 MB | `193BAB7A04FCA699A6C82E6ABB5B81BDB28177F0ABD4062552B04908DAFB19F8` / `9EF676D6ED3C88256A2D92C640C44F25B0C40947E111B14B8BE8F594091563E6` |
| PP-OCRv6 Small | ONNX DirectML/CPU | 7.7M parameters | Detector + recognizer + dictionary | 31.1 MB | `D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E` / `5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634` |
| PP-OCRv6 Medium | ONNX DirectML/CPU | 34.5M parameters | Detector + recognizer + dictionary | 138.6 MB | `EB13B44B25BB36F89528B68720AF8A61D9CF381176107F465DB1757B65D086E1` / `9C09ABF0957F7968C7586464B7397B84AD2387A0497A351AF40E9ACC71B673BA` |
| GLM-OCR | llama.cpp Vulkan/CPU | 1.33B parameters | Q8_0 model + Q8 projector | ~1.4 GB | `45BC244A6446AFF850521DC41F18BC8D7105AD5F0C2C8C28AF04E7CC4F4D50B1` / `9C4B58E33E316ED142EB5DCB41ABEC3844D3E6E5DC361FFB782C3FA9D175141F` |
| MiniCPM-OCR 1.0 | llama.cpp Vulkan/CPU | 1.3B parameters | Q4_K_M model + F16 projector | ~1.4 GB | `C80A703400633F298CC2FDDF6D493A541BE36918C0801C0B1BF3F14C5703CE49` / `CEED82261B3B1380C3165F8B37C10117A50093BE1362CBFEEB692FA036444682` |
| Xiaomi-OCR-0 | llama.cpp Vulkan/CPU | 0.8B parameters | Q4_K_M model + F16 projector | ~0.75 GB | `70256E1482297608B25A79DBD43DB794E75D38B4999B65FC4B75F9C66E348E1F` / `CD6A69A6FD4C36DBC5CA56BD1A5B9CFCA3DEB2043E645E95B8491E2FC0F29659` |

All three PP-OCRv6 tiers were inference-tested on both DirectML and CPU with real English/Italian pages, including accents, the euro sign, tables, lists, two-column order, and page breaks. Medium is the slowest tier; on the reference system an eight-page mixed workload took 15.7/10.4 s on DirectML and 18.5/18.5 s on CPU across two runs, with a 1,595 ms DirectML model load. Tiny and Small remain the better defaults for constrained systems. PP-OCRv6 models are Apache-2.0 and are downloaded explicitly; dictionaries are bundled.

The optional advanced engines are downloaded separately and are never installed by default. Their page-level pipeline uses a loopback-only llama.cpp server and does not require PP-DocLayoutV3. On the reference AMD GPU, three mixed pages produced DOCX and PDF in 12.8 s (GLM-OCR), 33.4 s (MiniCPM-OCR), and 6.1 s (Xiaomi-OCR-0), including model load but excluding the one-time download. These are page-level results, not a full layout-analysis benchmark. GLM-OCR is MIT; MiniCPM-OCR is Apache-2.0; Xiaomi-OCR-0 weights are Apache-2.0 and the tested GGUF conversion is by richardyoung.

## Documents

| Format | Extension | Input | Output | Notes |
|---|---|:---:|:---:|---|
| PDF | `.pdf` | Text extraction | Via Typst | Document conversion does not OCR scanned PDFs; use the OCR workspace with rasterized pages |
| DOCX | `.docx` | Yes | Yes | WordprocessingML; images, tables, headings preserved where Pandoc can map them |
| ODT | `.odt` | Yes | Yes | OpenDocument text |
| RTF | `.rtf` | Yes | Yes | Rich Text Format |
| TXT | `.txt` | Yes | Yes | Plain text; layout is intentionally not preserved |
| HTML | `.html` | Yes | Yes | Standalone, embedded resources |
| Markdown | `.md` | Yes | Yes | GitHub-flavoured Markdown output |

Document conversion uses Pandoc 3.13. PDF output uses Typst 0.15.1. PDF input is converted by extracting text with PdfPig; original PDF page layout is not round-tripped. Formatting loss is reported when the target cannot represent the source structure.

## E-books

| Format | Extension | Input | Output | Notes |
|---|---|:---:|:---:|---|
| EPUB | `.epub` | Yes | Yes | Chapter navigation, metadata, images, and basic formatting are preserved |
| Document inputs | `.docx`, `.odt`, `.html`, `.md`, `.txt`, `.pdf` | Yes | Via document engine | Used as source for EPUB conversion |

DRM-protected or encrypted e-books are detected and refused. PixelForge does not attempt to bypass DRM.

## Archives

| Format | Extension | Create | Repackage | Extract | Notes |
|---|---|:---:|:---:|:---:|---|
| ZIP | `.zip` | Yes | Yes | Yes | Deflate-based archive |
| 7Z | `.7z` | Yes | Yes | Yes | LZMA-family archive |
| TAR | `.tar` | Yes | Yes | Yes | Uncompressed archive |
| GZ | `.gz` | Yes | Single-entry | Yes | Single-file compression |
| BZ2 | `.bz2` | Yes | Single-entry | Yes | Single-file compression |
| XZ | `.xz` | Yes | Single-entry | Yes | Single-file compression |
| Zstandard | `.zst` | Yes | Single-entry | Yes | Uses bundled `zstd.exe` |

Archive extraction is validated before committing: path traversal/ZIP Slip, absolute paths, entry counts, expanded size, corruption, and single-entry constraints for stream compressors. Extraction writes a new folder and never modifies or deletes the source archive. Passwords are never logged or persisted.

## Media tools

### Audio tools

- EBU R128/LUFS two-pass integrated loudness measurement and normalization with true-peak protection.
- Spectral denoising (`afftdn`) and bundled RNNoise speech denoising (`arnndn`).
- Silence detection and removal with configurable threshold, minimum duration, and margin.
- Trim, merge, mono/stereo conversion, sample-rate conversion.
- Metadata and cover-art editing.
- Audio extraction from video with stream copy when the codec/container combination is compatible.

### Image tools

- Quality/lossless compression and format optimization.
- Deterministic batch pipeline: crop → resize → denoise → sharpen → colour → watermark → metadata → encode.
- EXIF/IPTC metadata editing and removal.
- Local u2netp background removal with transparent PNG/WebP/AVIF output, DirectML or CPU.

### Video tools

- Target-size compression with bitrate estimation, two-pass encoding where supported, and explicit oversize reporting.
- Codec-aware Maximum Quality, Balanced, Smallest File, and Web Optimized presets.
- Hardware encoder probing with CPU fallback; hardware is never claimed unless an actual encode succeeds.
- Trim (stream copy or frame-accurate), merge (safe stream copy or common-format re-encode), stabilization, thumbnails, GIF, audio extraction/replacement, subtitle extraction/conversion/embedding/dropping, metadata, HDR-to-SDR, and resolution/frame-rate conversion.

HDR-to-SDR requires a source with PQ/HLG or high-bit-depth HDR characteristics. An SDR input is refused rather than silently tone-mapped. The conversion uses explicit colorspace linearization, hable tone mapping, and BT.709 output tagging.

## AI upscaling and interpolation models

| Model | Task / scale | Architecture | File size | SHA-256 |
|---|---:|---|---:|---|
| Real-ESRGAN x4plus | 4× | RRDBNet, 23 blocks | 63.9 MB | `5C586662929CBC686C1A5C38D9C060DBDB4EA5863A1F7672B8C0761E6B89C033` |
| Real-ESRGAN x2plus | 2× | RRDBNet, 23 blocks | 64.0 MB | `7115BA92E8A1BFA63D68558EF006EF3D91273A068D321B1439F8BB1C9179002C` |
| Real-ESRGAN x4plus Anime 6B | 4× | RRDBNet, 6 blocks | 17.5 MB | `45BD54934AEABE8DF744C8FDACB9E8846C9B55CB4E60C499DB77405D1625A667` |
| Real-ESRGAN General x4 v3 | 4× | SRVGGNetCompact | 4.6 MB | `09B757ACCD747D7E423C1D352B3E8F23E77CC5742D04BAE958D4EB8082B76FA4` |
| Real-ESRGAN AnimeVideo v3 | 4× | SRVGGNetCompact | 2.4 MB | `78BAA685A1A92CAC6E14AB2AF2A8AD0EF56FA48629563B1414E3A7ACC54D86E2` |
| Waifu2x CUnet | 2×/4× | CUnet (NCNN/Vulkan) | 35.5 MB package | `3F60BA0B26763C602CB75178C2051BF0C46F3CC9D13975A052A902773988A34B` |
| Waifu2x Upconv Anime | 2×/4× | Upconv 7 anime (NCNN/Vulkan) | 35.5 MB package | same package hash |
| Waifu2x Upconv Photo | 2×/4× | Upconv 7 photo (NCNN/Vulkan) | 35.5 MB package | same package hash |
| Real-CUGAN SE | 2×/3×/4× | Cascade U-Net (NCNN/Vulkan) | 46.0 MB package | `C6E08D46C11704B1E3A1ADA9DDD591CB5005F52F132136C8633BA25DEF400E01` |
| Real-CUGAN Pro | 2×/3×/4× | Cascade U-Net Pro (NCNN/Vulkan) | 46.0 MB package | same package hash |
| SwinIR Real-SR x4 | 4× | SwinIR-M, fixed 64×64 tiles | 121.8 MB | `5505E2D8BACEAC077A0978CDAD5174F4D1EBA28DCD417607AED6A4FCD271FAE3` |
| SPAN 4× (Nomos Uni) | 4× | SPAN, fixed 256×256 tiles | 1.7 MB | `40D69000AF0139F1B0FEF058B187E11BADCC8BF2DCB904FE0112FEF289901778` |
| SCUNet GAN | 1× restoration | Swin-Conv-UNet denoiser | 91.3 MB | `79AE6073C91C2D25D1F199137A67C8D0F0807DF27219CDD7D890F3CC6D5B43E7` |
| NAFNet GoPro | 1× restoration | NAFNet width64 deblurrer | 275.4 MB | `00C07294585512D162BC2313A2A12CD8D9CD532D0981D0D01F444BD48C4330CD` |
| RIFE 4.25 (stable ID `rife-4.25-lite`) | Frame interpolation | IFNet | 21.6 MB | `702802D0862AB78F2D72133C6BC3768E7E89B81756F7CD51D1055A2FE3D06577` |
| PP-OCRv6 Tiny | OCR | DB detector + CTC recognizer | 6.2 MB total | See OCR section above |
| PP-OCRv6 Small | OCR | DB detector + CTC recognizer | 31.1 MB total | See OCR section above |
| PP-OCRv6 Medium | OCR | DB detector + CTC recognizer | 138.6 MB total | See OCR section above |
| GLM-OCR / MiniCPM-OCR / Xiaomi-OCR-0 | Advanced OCR | VLM + vision projector (llama.cpp) | ~0.75–1.4 GB each | See OCR section above |

- ONNX inputs: RGB float32, NCHW, values 0–1. Real-ESRGAN is dynamic; SwinIR is fixed 64×64 and SPAN is fixed 256×256. SCUNet uses padded 256×256 tiles; NAFNet is dynamic.
- Work is split across three backends: ONNX Runtime DirectML/CPU, NCNN/Vulkan, and llama.cpp/Vulkan. Each backend is installed only when a chosen model needs it.
- Output: RGB float32; 2×/3×/4× for upscalers, or the source resolution for 1× restoration. Restoration models are not upscalers and are labelled separately in the UI.
- RIFE input: two RGB float32 NCHW frames plus a timestep, padded to multiples of 64; output is one intermediate frame. It does not change resolution.
- Execution: DirectML, Vulkan, or CPU depending on the model; a forced unsupported provider is reported or rejected. SCUNet is CPU-only after a verified native DirectML crash.
- Tiling: automatic or explicit tile size/overlap for ONNX; fixed-tile padding/cropping for SwinIR, SPAN, and SCUNet; NCNN handles its own tiling.
- Output formats: same as source by default, or any supported image output.
- Alpha: preserved where selected; ICC/profile and metadata preservation are configurable. NCNN outputs PNG with alpha when the input has alpha.
- Animated, multipage, and vector inputs are rejected by the upscaler/restoration workspace; convert/rasterize them first.

Reference performance: 640 × 420 → 2560 × 1680 with Real-ESRGAN x4plus on an AMD Radeon RX 7600 XT through DirectML completed in approximately 3.7 seconds during UI verification. CPU performance is substantially slower and depends on the processor.

The automatic DirectML tile size was benchmarked with a four-image batch (3200×4800, 1920×1280, 2560×1680, and 3600×2400 output dimensions), Real-ESRGAN x4plus, PNG output, and two-item overlap:

| Tile size | Total for four images |
|---:|---:|
| 128 px | 29.4 s |
| 256 px | 18.5 s |
| 512 px | 15.7 s |
| 640 px | 15.5 s |
| 768 px | 69.1 s (VRAM pressure) |
| 1024 px | 30.1 s |

At 512 px, two-item overlap measured 16.3 s versus 21.5 s serialized. The final repeat run at the 512 px default completed in 14.768 s (3.692 s/image) and verified all four output dimensions. The default is 512 px; larger tiles are not universally faster and can trigger VRAM pressure.

Additional reference measurements on the AMD Radeon RX 7600 XT (same machine, headless harness):

| Model | Input → output | Backend | Time |
|---|---|---|---:|
| Waifu2x CUnet, 2× | 900×600 → 1800×1200 | Vulkan | 2.1 s |
| Waifu2x CUnet, 4× | 900×600 → 3600×2400 | Vulkan | 2.8 s |
| Waifu2x CUnet, 2× | 640×420 alpha → 1280×840 | Vulkan | 0.8 s |
| Waifu2x CUnet, 2× | 900×600 → 1800×1200 | CPU | 4.7 s |
| Real-CUGAN SE, 3× | 900×600 → 2700×1800 | Vulkan | 3.7 s |
| SwinIR Real-SR, 4× | 128×96 → 512×384 | DirectML | 2.2 s |
| SPAN 4× | 128×96 → 512×384 | DirectML | 0.49 s |
| SCUNet GAN, 1× | 128×96 → 128×96 | CPU | 5.3 s |
| NAFNet GoPro, 1× | 128×96 → 128×96 | DirectML | 2.1 s |
| GLM-OCR, 3 pages | DOCX + PDF | llama.cpp Vulkan | 12.8 s |
| MiniCPM-OCR, 3 pages | DOCX + PDF | llama.cpp Vulkan | 33.4 s |
| Xiaomi-OCR-0, 3 pages | DOCX + PDF | llama.cpp Vulkan | 6.1 s |

These are single-run reference measurements, not universal performance promises. CPU-only and low-memory systems will be slower. SCUNet is CPU-only because DirectML inference produced a native ONNX Runtime access violation during verification; NAFNet is the DirectML-capable deblurring path.

Upscayl's "Digital Art" is not a separate model: it is the official Real-ESRGAN Anime 6B NCNN weights under BSD-3-Clause, already supported by PixelForge's Anime 6B ONNX entry. RIFE 4.26/Lite remains unimplemented because no verified ONNX export or integrated NCNN video pipeline is available. HAT, SUPIR, VOSR 2.0, and VARestorer were excluded for memory/runtime/license reasons. The model catalogue in the application source is the authoritative record of supported model IDs and hashes.

## Unsupported / intentionally excluded

- Cloud or remote processing.
- Bundling AI upscaling, interpolation, or OCR model weights (downloads are explicit and SHA-256 verified; u2netp and RNNoise are the bundled exceptions).
- HEIC/HEIF, SVG, and PSD output.
- Editing the original source file in place.
- GPU video encode paths if the hardware/driver probe fails.
- Animated input in the AI upscaler without explicit conversion/flattening.

