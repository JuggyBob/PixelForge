# Security Policy

## Supported versions

Security fixes are applied to the latest public release. The current release is
PixelForge 2.0.0. Version 1.0.0 is no longer supported.

## Reporting a vulnerability

Do not open a public issue containing exploit details, credentials, personal
data, or reproduction files that should not be public.

Use GitHub's **private vulnerability reporting** on this repository
(Security → Advisories → Report a vulnerability). If private reporting is
unavailable, open a minimal public issue asking for a private contact channel
without including technical details.

A useful report includes:

- PixelForge version and Windows version.
- Whether the setup installer or portable package was used.
- Relevant configuration and runtime/provider information.
- Clear reproduction steps.
- Expected and actual behavior.
- Sanitized logs or screenshots, with usernames and personal paths removed.
- Any known preconditions or affected file types.

## Scope

Security reports are especially relevant for:

- Unsafe archive extraction or path handling.
- Command injection or argument-handling flaws in external-engine calls.
- Model/runtime download verification failures or path traversal.
- Unsafe handling of untrusted media or document files.
- Local data exposure, credential leakage, or unintended network access.
- Installer, uninstaller, shortcut, or update integrity issues.

General crashes, unsupported formats, performance problems, and feature requests
should use the normal issue templates.

## What to avoid

- Do not include real personal or customer media in a report.
- Do not publish a working exploit before a fix is available.
- Do not test against systems or data you do not own or have permission to use.

## Handling

Reports are reviewed privately. The maintainers will validate the issue,
determine affected versions, prepare a fix where possible, and coordinate
disclosure. Response times are best-effort and are not guaranteed. Credit is
given when requested and appropriate.
