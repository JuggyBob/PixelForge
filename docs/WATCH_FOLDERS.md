# Watch folders

Watch Folders polls an input folder, waits for each file to become stable, and
enqueues ordinary queue jobs. Source files are never modified or deleted.

## Inline editor

Each rule is one compact row: identifying information on the left, then Delete,
the enable toggle, and a chevron pinned to the far right of the row. Clicking
anywhere else on the row expands the editor underneath it. The editor animates
its actual height and opacity; repeated clicks complete the current transition
safely, and saving a rule updates the existing row in place so the editor can be
opened again immediately.

The collapsed row no longer shows input/output paths or a redundant paused/active
label. The editor contains the rule name, both directory fields with Open
buttons, subfolder and processed-file settings, operation settings, model
selectors, Save, and Cancel. Cancel restores the saved values; Save validates and
persists.

## Model selection

There are no free-text model identifiers. Selectors are populated from the real
model catalogue:

- Traditional image upscaling: Nearest Neighbor, Bilinear, Bicubic, Lanczos,
  Mitchell–Netravali.
- AI image upscaling: image-upscale/restore models that support still images.
- Video super-resolution: image-upscale models that support video frames.
- Frame interpolation: frame-interpolation models that support video frames.

Installed and valid models sort first. Missing models remain selectable so the
rule is preserved and reports the dependency instead of silently substituting.
Each selector has a **Manage models…** action that opens the shared right-side
Model Management panel with the correct filter.

## Operations

- Image, audio, video, document, e-book, and archive conversion.
- Traditional and AI image upscaling.
- Video upscaling with optional RIFE 4.25 interpolation and temporal stability.

Watch rules use the same typed settings objects as the dedicated pages.

## Processed-file suppression

**Ignore previously processed files** is enabled by default and persists across
restarts. A file is marked processed only after its job completes successfully
with no failed or cancelled items. Failed, cancelled, and interrupted jobs do not
become permanently suppressed. Dependency failures (missing model, runtime,
provider, or invalid scale) are retried after the dependency is fixed.

## Safety

- Input and output folders cannot be the same or nested inside each other.
- Files under the output folder are excluded from input scans.
- Temporary files and duplicate filesystem observations are ignored.
- Rules referencing a missing model or runtime remain visible and report the
  dependency rather than silently substituting another model.
- Editing an active rule clears pending/processed state when its operation or
  paths change, and persists the cleared state before monitoring resumes.
