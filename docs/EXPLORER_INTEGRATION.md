# Explorer integration

The MSI registers a per-user **PixelForge** submenu for supported file extensions
and a folder command. Selecting a command activates the running PixelForge
instance (or launches it), navigates to the requested page, selects the requested
category, and stages the selected files in the page's input list. It never
auto-queues or starts processing.

## Commands

| Selection | Menu actions |
|---|---|
| Raster images (including HEIC/HEIF) | Convert image, upscale image, OCR image |
| Vector/multipage images | Convert image only (upscale/OCR reject them) |
| GIF | Convert image, convert video, upscale video |
| Audio | Convert audio |
| Video | Convert video, upscale video |
| Documents/e-books | Convert document, convert e-book |
| Archives | Process archive |
| Folder | Scan folder as images, recursively |

Each extension is registered under
`HKCU\Software\Classes\SystemFileAssociations\<extension>\shell\PixelForge`.
Irrelevant categories are therefore absent from the menu. The extension groups
are defined in `packaging/ContextMenu.wxs`; the current list covers the formats
accepted by `FormatCatalog` and `ContentFormatCatalog`.

## Activation protocol

Registry commands invoke:

```text
PixelForge.exe --shell <verb> "%1"
```

The C# activation path strips the `--shell` sentinel, validates the verb, filters
missing paths, and buffers the request until the shell has completed its initial
navigation. A second instance forwards the same arguments over a per-user named
pipe. The application then navigates and stages files via the page view models.

## Windows 11

These are classic registry verbs. On Windows 11 they appear under
**Show more options** (or **Shift+F10**), not in the compact top-level context
menu. Adding them to the compact menu requires a packaged `IExplorerCommand`
extension, which is outside the current unpackaged distribution and would
require package identity.

## Multi-selection

`MultiSelectModel=Player` passes the selected paths in one invocation, and the
activation path preserves all existing paths. The practical limit is the Windows
command-line length; very large selections should use the folder command.

## Installation and removal

The MSI installs per user to `%LOCALAPPDATA%\Programs\PixelForge`. Windows exposes
the product's Add/Remove Programs entry in HKLM for this dual-purpose package,
while the per-user registration is under
`HKCU\Software\Microsoft\Installer\Products`. Uninstall removes the MSI-owned
verbs, shortcuts, and payload. The Inno Setup EXE remains available; the MSI
refuses to install over an existing Inno installation.
