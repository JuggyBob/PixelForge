# Update checking

PixelForge can check the official GitHub repository for a newer stable
release. Update checking is **opt-in**. The default is disabled and no update
request is made unless the user clicks **Check for updates** or explicitly
enables the startup check.

## Source and privacy

- Source: `https://api.github.com/repos/JuggyBob/PixelForge/releases/latest`.
- No GitHub token is embedded or required.
- The request sends no media, filenames, history, telemetry, or unique user ID.
- A manual check works even when the startup toggle is off.
- Startup checks run once; the app deduplicates the notification by release
  version and never checks from two independent startup paths.

## States

The checker distinguishes:

1. update available;
2. already up to date;
3. running a newer PixelForge build whose base version is equal or lower;
4. no release published (404);
5. GitHub rate limit (403/429);
6. network/timeout failure;
7. GitHub server failure (5xx);
8. invalid/unparsable release metadata;
9. no compatible release asset;
10. unexpected response.

A valid older release is **not** an error. For a 2.0.0 build, the public v1.0.0
release is reported as "No newer stable public release is available."

## Behavior

- Versions use Semantic Versioning, not string comparison.
- Builds compare the public base version without any prerelease suffix.
- Results are cached with an ETag; a 304 without a cache is handled explicitly.
- Requests have a 15-second timeout and respect cancellation.
- Invalid release hosts/schemes are rejected before any URL is opened.
- The app never downloads or installs an update automatically; it opens the
  release page or the compatible installer/portable asset in the browser.
- Release notes are rendered through a limited, safe Markdown renderer. Raw HTML
  is never rendered.

## Runtime upgrades

A PixelForge update can require a newer optional runtime without changing the
model files or the user profile. On first launch after such an update, if an
older llama.cpp runtime directory is present and the current runtime is missing,
the app downloads the pinned, SHA-256-verified runtime in the background, shows
progress in the notification center, and removes the superseded runtime
directory after a successful install. Existing OCR models, settings, history,
and watch rules are left untouched.

## Release notes

The popup supports headings, lists, emphasis, inline code, fenced code, and
http(s) links from the official GitHub release body. Release content is never
treated as arbitrary HTML.
