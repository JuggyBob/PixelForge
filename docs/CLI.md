# PixelForge CLI

`pixelforge.exe` is the headless entry point for PixelForge. It uses the same
composition root, validators, queue, engines, settings, history, and model
manager as the desktop application. It does not create a WinUI window.

## Usage

```text
pixelforge convert <images|audio|video|documents|ebooks|archives> --input <path> [options]
pixelforge upscale image --input <path> [options]
pixelforge upscale video --input <path> [options]
pixelforge ocr --input <page-image> [options]
pixelforge tools <audio|image|video> --tool <id> --input <path> [options]
```

Run `cli\pixelforge.exe --help` (or add the installed `cli` folder to PATH) or a command-specific help such as
`pixelforge convert images --help`.

The CLI is a self-contained single-file executable with the native image/ONNX libraries embedded. On first use, .NET extracts those native libraries to the user temp folder. Keep the companion OCR dictionary files beside `pixelforge.exe`; copying only the executable breaks OCR dictionary installation.

## Global behavior

| Option | Meaning |
|---|---|
| `--data-dir <dir>` | Use an isolated PixelForge profile |
| `--output-dir <dir>` | Destination directory |
| `--format <id>` | Target format |
| `--collision <unique\|skip\|overwrite>` | Output collision policy |
| `--suffix <text>` | Output filename suffix |
| `--recursive` | Include nested files for directory inputs |
| `--flatten` | Keep one frame/page from animated or multipage images |
| `--json` | Machine-readable result on stdout; progress goes to stderr |
| `--verbose` | Print exception details |
| `-h`, `--help` | Root or command help |
| `-v`, `--version` | Print the application version |

Exit codes: `0` success, `1` processing failure, `2` usage or validation error,
`130` cancelled.

## Commands

### `convert`

```text
pixelforge convert <images|audio|video|documents|ebooks|archives> --input <path> [options]
```

`--input` may be repeated and may name a file or a directory. Without
`--recursive`, only the top level of a directory is scanned. Archive creation is
the exception: a directory is packaged as a single archive, not scanned.

| Option | Meaning |
|---|---|
| `--format <id>` | Target format (default depends on category) |
| `--quality <number>` | Image/video quality |
| `--bitrate <kbps>` | Audio/video bitrate |
| `--codec <id>` | Audio/video codec; the default is the first compatible codec for the chosen format |
| `--encoder <id>` | Specific FFmpeg encoder when a codec has more than one |
| `--strip-metadata` | Remove metadata where the format supports it |
| `--flatten` | Flatten animated or multipage images to one frame/page |
| `--action <create\|repackage\|extract>` | Archive operation (default `create`) |
| `--compression <0-19>` | Archive compression level |
| `--collision`, `--suffix`, `--output-dir`, `--json` | See global options |

For archives, a directory input is packaged as one archive (default `create`).
For all other categories, a directory input is scanned for compatible files.

### `upscale image`

```text
pixelforge upscale image --input <path> --method <ai|traditional> [options]
```

| Option | Meaning |
|---|---|
| `--method <ai\|traditional>` | Upscaling method (default `ai`) |
| `--model <id>` | AI model id from the catalogue |
| `--algorithm <name>` | Traditional resampling algorithm |
| `--scale <number>` | Scale factor |
| `--provider <auto\|cpu\|directml\|vulkan>` | Execution provider |
| `--tile <px>` / `--overlap <px>` | Tiling controls |
| `--noise <0-3>` | NCNN denoise level |
| `--format <id>` / `--quality <n>` | Output format and quality |

### `upscale video`

```text
pixelforge upscale video --input <path> [options]
```

| Option | Meaning |
|---|---|
| `--image-model <id>` | Frame super-resolution model |
| `--interpolation-model <id>` | RIFE model |
| `--interpolate` | Enable frame interpolation |
| `--fps-multiplier <2-4>` | Frame-rate multiplier |
| `--temporal` | Enable temporal stability |
| `--temporal-strength <0-100>` | Temporal stability strength |
| `--bitrate <kbps>` | Output video bitrate |
| `--no-super-resolution` | Disable frame super-resolution |

### `ocr`

```text
pixelforge ocr --input <page-image> [--input <page-image> ...] [options]
```

| Option | Meaning |
|---|---|
| `--model <id>` | OCR model id |
| `--pdf` | Also generate a PDF |
| `--provider <auto\|cpu\|directml>` | Execution provider |
| `--name <document-name>` | DOCX base name |
| `--output-dir <dir>` | Destination directory |
| `--review` | Rejected headless; use the desktop review dialog |

Pages are processed in the order supplied. The DOCX and optional PDF are
written to the output directory and recorded in history like any other job.

### `tools`

```text
pixelforge tools <audio|image|video> --tool <id> --input <path> [options]
```

`--tool` accepts an id from `MediaToolCatalog` (for example
`NormalizeLoudness`). Category-specific options:

| Category | Options |
|---|---|
| Audio | `--target-lufs <db>`, `--true-peak <db>`, `--noise-method <spectral\|rnnoise>`, `--noise-db <db>`, `--trim-start <seconds>`, `--trim-end <seconds>` |
| Video | `--bitrate <kbps>`, `--audio-bitrate <kbps>`, `--target-size <mb>`, `--trim-start <seconds>`, `--trim-end <seconds>` |
| Image | `--format`, `--quality`, `--output-dir` |

## Examples

```powershell
pixelforge convert images --input photo.png --format webp --quality 90 --output-dir out
pixelforge convert audio --input song.wav --format flac --output-dir out
pixelforge convert video --input clip.mp4 --format mp4 --codec h265 --bitrate 12000 --output-dir out
pixelforge upscale image --input art.png --method ai --model realesrgan-x4plus --scale 4 --output-dir out
pixelforge upscale video --input clip.mp4 --scale 4 --temporal --output-dir out
pixelforge ocr --input page-1.png --input page-2.png --pdf --output-dir out
pixelforge tools audio --tool NormalizeLoudness --input song.wav --target-lufs -16 --output-dir out
```

Directory inputs are scanned through the same folder scanner used by the GUI.
Unsupported files are rejected by the same validators. Animated or multipage
images must be flattened explicitly with `--flatten` when the target format
cannot preserve them. Output collisions are never overwritten unless
`--collision overwrite` is explicitly supplied.

## Queue and history

CLI jobs are ordinary queue jobs. They are validated, executed, verified, and
recorded in the same SQLite history. Progress is written to stderr so `--json`
stdout remains machine-readable. Ctrl+C cancels the active job and propagates
cancellation into external processes.

The CLI does not start a hidden GUI. Interactive previews and review dialogs are
not available in headless mode; use the desktop application for those steps.

## Automation notes

- `--json` emits a single indented object: `jobId`, `state`, `operation`,
  `success`, `summary`, and `items` (input/output paths, status, error,
  error category, requested/actual provider, warning, duration). Human-readable
  progress goes to stderr.
- `--data-dir` creates or reuses a complete profile (settings, history, models,
  runtimes) without touching the desktop profile.
- Machine-specific model and runtime downloads are not performed implicitly;
  install them in the desktop application first, or point `--data-dir` at a
  profile that already contains them.
- A directory input that contains no supported files is a validation error and
  exits with code `2`.
