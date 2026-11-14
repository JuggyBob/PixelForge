# Third-Party Notices

PixelForge itself is licensed under the Apache License 2.0. See `LICENSE` and
`NOTICE`. Third-party components remain under their own licenses; the PixelForge
license does not replace those terms.

This document covers the components redistributed in the PixelForge 1.0.0
installer and portable archive, plus optional runtimes and model weights that
users download explicitly. Full license texts are in `licenses/`. Component
license files shipped beside the binaries (for example `ffmpeg/LICENSE.txt`,
`documents/pandoc/COPYING.rtf`, and `archives/LICENSE.txt`) are also part of the
distribution.

## Redistributed runtime components

| Component | Version | License | Distribution | Source |
|---|---|---|---|---|
| PixelForge | 1.0.0 | Apache-2.0 | Application | https://github.com/JuggyBob/PixelForge |
| FFmpeg / FFprobe | 9.0.2 | LGPL-3.0 (custom build, `--disable-gpl --disable-nonfree`) | Bundled executables and shared libraries | https://ffmpeg.org/ |
| Pandoc | 3.13 | GPL-2.0-or-later | Bundled separate executable | https://github.com/jgm/pandoc |
| Typst | 0.15.1 | Apache-2.0 | Bundled separate executable | https://github.com/typst/typst |
| 7-Zip | 26.06 | LGPL-2.1-or-later with unRAR restriction | Bundled executables and DLLs | https://www.7-zip.org/ |
| Zstandard | 1.5.7 | BSD-3-Clause | Bundled `zstd.exe` | https://github.com/facebook/zstd |
| Magick.NET-Q16-x64 | 15.1.0 | Apache-2.0 (wrapper); ImageMagick License (native) | NuGet / native library | https://github.com/dlemstra/Magick.NET |
| ImageMagick | 7.1.2-35 Q16 | ImageMagick License | Native library via Magick.NET | https://imagemagick.org/ |
| ONNX Runtime | 1.24.4 | MIT | NuGet / native library | https://github.com/microsoft/onnxruntime |
| DirectML | 1.15.x redistributable | Microsoft DirectML license terms | NuGet / native library | https://learn.microsoft.com/windows/ai/directml/ |
| Windows App SDK | 2.6.5 | Microsoft Software License Terms | NuGet / self-contained runtime | https://github.com/microsoft/WindowsAppSDK |
| .NET Runtime | 10.0.x | MIT | Self-contained runtime | https://github.com/dotnet/runtime |
| PdfPig | 0.1.11 | Apache-2.0 | NuGet | https://github.com/UglyToad/PdfPig |
| Microsoft.Data.Sqlite | 10.0.15 | MIT | NuGet | https://github.com/dotnet/efcore |
| SQLite | 3.x | Public domain | Native library via SQLitePCLRaw | https://sqlite.org/ |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | NuGet | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.* | 10.0.15 | MIT | NuGet | https://github.com/dotnet/runtime |
| JetBrains Mono | 2.304 | SIL Open Font License 1.1 | Bundled font | https://github.com/JetBrains/JetBrainsMono |
| RNNoise model (`std.rnnn`) | Xiph reference model | BSD-3-Clause | Bundled model | https://github.com/xiph/rnnoise |
| u2netp segmentation model | nfa04 ONNX conversion | Apache-2.0 | Bundled model | https://huggingface.co/nfa04/u2netp-onnx |
| PaddleOCR dictionaries | PP-OCRv6 resources | Apache-2.0 | Bundled dictionaries | https://github.com/PaddlePaddle/PaddleOCR |

## FFmpeg

The bundled FFmpeg/FFprobe build is an LGPL shared build produced from the
official FFmpeg 9.0.2 source with the BtbN win64 LGPL shared build recipe. It is
configured without GPL-only or non-free components. FFmpeg is invoked as a
separate process; PixelForge communicates with it through command-line
arguments and standard streams.

The corresponding FFmpeg source is available at
https://ffmpeg.org/download.html. The build recipe and release packaging are
published by https://github.com/BtbN/FFmpeg-Builds. The applicable LGPL text is
in `licenses/FFmpeg-LICENSE.txt` and is installed beside the executables as
`ffmpeg/LICENSE.txt`.

The build links component libraries whose licenses remain with their upstream
projects, including libaom, dav1d, libiconv, Kvazaar, LAME, OpenH264, Opus,
SVT-AV1, Vorbis/libogg, Intel VPL, libvpx, libxml2, zlib, Crypto++, mpg123,
MinGW-w64, and the GCC runtime. Their upstream projects and license texts are
listed with the FFmpeg build sources.

## Pandoc and Typst

Pandoc 3.13 is redistributed unmodified as a separate executable under
GPL-2.0-or-later. The complete corresponding source is available from the
official Pandoc 3.13 release and repository:
https://github.com/jgm/pandoc/releases/tag/3.13. Pandoc's COPYING and COPYRIGHT
files are installed under `documents/pandoc/` and copied to
`licenses/COPYING.rtf` and `licenses/COPYRIGHT.txt`.

Typst 0.15.1 is redistributed unmodified under Apache-2.0 for PDF generation.
Its license and notice are installed under `documents/typst/` and copied to
`licenses/Typst-LICENSE` and `licenses/Typst-NOTICE`.

## 7-Zip and Zstandard

7-Zip 26.06 is distributed under LGPL-2.1-or-later with the unRAR restriction.
The license files are installed under `archives/` and copied to
`licenses/7-Zip-LICENSE.txt` and `licenses/7-Zip-LICENSE-7z.txt`.

Zstandard 1.5.7 is distributed under BSD-3-Clause. Its COPYING and license
files are installed under `archives/` and copied to
`licenses/Zstandard-COPYING.txt` and `licenses/Zstandard-LICENSE.txt`.

## Magick.NET and ImageMagick

Magick.NET-Q16-x64 is an Apache-2.0 wrapper around the native ImageMagick
library, which is distributed under the ImageMagick License. The native library
is loaded locally for image decoding, conversion, and processing. The complete
notice bundle from the Magick.NET package is in
`licenses/Magick.NET-NOTICE.txt`.

## ONNX Runtime, DirectML, and Windows App SDK

ONNX Runtime is licensed under MIT; `licenses/ONNX-Runtime-LICENSE.txt`
contains the text. DirectML and the Windows App SDK are Microsoft components
governed by Microsoft's redistributable license terms; the Windows App SDK
license and notice are included as `licenses/WindowsAppSDK-LICENSE.txt` and
`licenses/WindowsAppSDK-NOTICE.txt`.

The .NET runtime, Microsoft.Data.Sqlite, CommunityToolkit.Mvvm, and the
Microsoft.Extensions packages are MIT-licensed; a common MIT text is in
`licenses/MIT.txt`. SQLite is in the public domain; see
`licenses/SQLite-PUBLIC-DOMAIN.txt`.

## Fonts and bundled assets

JetBrains Mono 2.304 is distributed under the SIL Open Font License 1.1. The
font and its OFL text are installed under `Assets/Fonts/`; the license is also
copied to `licenses/JetBrainsMono-OFL.txt`.

The application artwork, icon, and UI assets are part of the PixelForge project
unless a different attribution is stated in the asset directory.

## Bundled models

PixelForge bundles only the small models needed for offline helpers:

- **RNNoise `std.rnnn`** — BSD-3-Clause; license in
  `licenses/RNNoise-LICENSE.txt`.
- **u2netp ONNX segmentation model** — Apache-2.0; license in
  `licenses/u2netp-LICENSE.txt`.
- **PP-OCRv6 dictionaries** — Apache-2.0; license in
  `licenses/PaddleOCR-LICENSE.txt`.

Large AI, restoration, interpolation, and OCR model weights are **not** included
in the installer or portable archive. They are downloaded only after an explicit
user action and verified by SHA-256 before installation. The model catalogue in
the application source records each model's upstream source, license, size, and
hash.

## Optional downloaded runtimes

These packages are downloaded on demand and are not part of the installer:

| Runtime | Version | License | Source |
|---|---|---|---|
| waifu2x-ncnn-vulkan | 2022-07-28 | MIT | https://github.com/nihui/waifu2x-ncnn-vulkan |
| realcugan-ncnn-vulkan | 2022-07-28 | MIT (wrapper and Real-CUGAN weights) | https://github.com/nihui/realcugan-ncnn-vulkan |
| llama.cpp Vulkan package | b12110 | MIT | https://github.com/ggml-org/llama.cpp |

## Optional model weights

| Model family / model | License | Upstream |
|---|---|---|
| Real-ESRGAN x4plus, x2plus, Anime 6B, General x4 v3, AnimeVideo v3 | BSD-3-Clause | https://github.com/xinntao/Real-ESRGAN |
| Waifu2x CUnet / Upconv Anime / Upconv Photo | MIT | https://github.com/nihui/waifu2x-ncnn-vulkan |
| Real-CUGAN SE / Pro | MIT | https://github.com/bilibili/ailab |
| SwinIR Real-SR x4 | Apache-2.0 | https://github.com/JingyunLiang/SwinIR |
| SPAN 4x Nomos Uni | CC-BY-4.0 | https://github.com/hongyuanyu/SPAN |
| SCUNet GAN | Apache-2.0 | https://github.com/cszn/SCUNet |
| NAFNet GoPro | MIT | https://github.com/megvii-research/NAFNet |
| RIFE 4.25 | MIT | https://github.com/hzwer/Practical-RIFE |
| PP-OCRv6 Tiny / Small / Medium | Apache-2.0 | https://github.com/PaddlePaddle/PaddleOCR |
| GLM-OCR | MIT | https://huggingface.co/ggml-org/GLM-OCR-GGUF |
| MiniCPM-OCR 1.0 | Apache-2.0 | https://github.com/OpenBMB/MiniCPM-OCR |
| Xiaomi-OCR-0 | Apache-2.0 | https://github.com/xiaomi-research/Xiaomi-OCR-0 |

SPAN requires attribution: the base weights are
`Phips/4xNomosUni_span_multijpg`, the ONNX export is by Tomoali, and the
architecture is by the SPAN authors. Attribution is retained in the model
library and in this notice.

## Development and test dependencies

xUnit, FlaUI, System.Drawing.Common, and the .NET test SDK are used only for
development and testing and are not part of the installed application.

## Trademarks

Windows, DirectML, DirectX, Visual Studio, and ONNX Runtime are trademarks or
products of Microsoft Corporation. AMD, Radeon, NVIDIA, and Intel are trademarks
of their respective owners. All other trademarks belong to their respective
owners.
