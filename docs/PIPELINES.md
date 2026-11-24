# Pipelines

Pipelines combine PixelForge operations into reusable, sequential visual workflows. Open **Pipelines** in the sidebar (under Converter) or press `Ctrl+Shift+P`.

## Execution model

A pipeline is always a single chain:

```text
Input -> operation -> operation -> ... -> Output
```

Branches, loops, and parallel graph paths are rejected by validation. Each operation is submitted to the normal PixelForge queue as a separate job. This means pipelines automatically reuse the existing validation, concurrency limits, cancellation, crash recovery, history, verification, and temporary-file handling. Intermediate results are chained through:

```text
%LOCALAPPDATA%\PixelForge\temp\pipelines\<run id>
```

Intermediate files are removed after the run unless **Keep intermediate files** is enabled on the Output node.

## Input

- Add individual files with **Choose files**, or pick a folder with **Choose folder**.
- Folders are rescanned when the pipeline runs, so new files are picked up automatically.
- Dropping files or a folder onto the canvas adds them to the Input node.
- All selected files in a batch must have the same media type.
- OCR currently supports one input file per pipeline because multiple pages must be reviewed as a single document.

## Operations

The action library is filtered by the current media type. Operations include:

- Images: convert, resize, AI upscale, traditional upscale (nearest, bilinear, bicubic, Lanczos, or Mitchell-Netravali), crop/adjust, optimise, background removal, metadata editing.
- Video: transcode, trim/compress/stabilise/tone-map, AI upscale, frame interpolation, thumbnail, GIF.
- Audio: convert, trim, normalise, denoise, remove silence, channel/rate conversion, extract from video.
- Documents and e-books: convert, OCR to Word/PDF.
- Archives: create, repackage, extract.

Archive extraction produces a folder and must be the final operation.

Drag an action card onto the canvas to add an independent node at the drop position; a dashed outline previews where the card will land. New nodes start unconnected; drag from an output port to a compatible input node or the Output node to wire them. Use the connection context menu or select a wire and press Delete to disconnect it.

The editor canvas is camera-based rather than a fixed-size surface. Pan by dragging empty space, zoom with the mouse wheel or the toolbar buttons, and use **Fit to view** to frame the whole graph. The grid and off-screen nodes are generated/culled on demand, so nodes remain usable far outside the initial viewport. Expanding a node temporarily shifts only the cards whose bounds actually intersect the expanded settings panel, with the smallest outward movement that clears the overlap; persisted node coordinates are unchanged.

## Output

The Output node controls:

- **Destination folder**
- **File name suffix**
- **Collision policy**: unique name, skip, or replace
- **Preserve folder structure** for folder input
- **Keep intermediate files**

Output files are committed by the queue only after verification. A failed file does not stop the rest of the batch.

## Saving

Pipelines are stored in `pipelines.json` beside `settings.json` (versioned, atomic writes, stable action IDs). The editor autosaves after changes, and also flushes when you leave the page. Undo and redo are available in the editor toolbar. Dragging a node changes only its visual position; connection order determines execution order.

Built-in templates are available when creating a pipeline. Templates only reference registered action IDs, so they remain valid across UI languages.

## Limitations

- One linear path only; no branching or loops.
- OCR pipelines run with a single input file.
- Archive extraction is terminal because it produces a folder.
- Rotate/flip are not pipeline actions.
- A pipeline step uses the same engine and settings model as the corresponding standalone tool; unsupported configurations are reported by normal queue validation.
