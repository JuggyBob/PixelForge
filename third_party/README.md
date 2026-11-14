# Third-party assets

Only small, redistributable assets required for a source build are checked in:

| Folder | Contents |
|---|---|
| `archives/` | 7-Zip and Zstandard executables plus licenses |
| `audio/rnnoise/` | Bundled RNNoise `std.rnnn` model and license |
| `models/segmentation/` | Bundled u2netp ONNX background-removal model and license |
| `fonts/JetBrainsMono/` | Bundled JetBrains Mono font and OFL license |
| `documents/pandoc/` | Pandoc license texts only; the executable is fetched |
| `documents/typst/` | Typst license and notice only; the executable is fetched |
| `ffmpeg/` | FFmpeg license only; the executables are fetched |

Large engines, downloaded model weights, and cached development models are not
stored in the repository. Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-dependencies.ps1 -SkipModels
```

The release packages include the verified engine binaries and the small bundled
models. Optional AI models remain user-downloaded and SHA-256 verified.
