# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

PixelForge release binaries are built from this public repository by GitHub
Actions on GitHub-hosted runners, uploaded to GitHub as workflow artifacts, and
then submitted to SignPath for signing. SignPath verifies the workflow origin
and requires manual approval for every signing request.

## Team roles

PixelForge is currently maintained by one person. The same person holds each
role until additional maintainers are added.

| Role | Member |
|---|---|
| Authors and committers | [JuggyBob](https://github.com/JuggyBob) |
| Reviewers | [JuggyBob](https://github.com/JuggyBob) |
| Approvers | [JuggyBob](https://github.com/JuggyBob) |

Changes from contributors who are not committers are reviewed before merge.
Each release signing request is approved explicitly by the approver.

## What is signed

| Artifact | Signature |
|---|---|
| `PixelForge.exe` | Authenticode, timestamped |
| `PixelForge-Setup-<version>-x64.exe` | Authenticode, timestamped |
| `PixelForge.exe` inside the portable ZIP | Authenticode, timestamped |
| Portable ZIP itself | Not an Authenticode format; the contained executable is signed |

Third-party executables and libraries redistributed with PixelForge, such as
FFmpeg, Pandoc, Typst, 7-Zip, and Zstandard, remain under their upstream
publishers and are not signed with the PixelForge/SignPath certificate. The
Inno Setup uninstaller stub generated on the user's machine is not covered by
the setup executable's signature.

The certificate is issued to **SignPath Foundation**, not to PixelForge. Windows
will display SignPath Foundation as the publisher. The signature links the
binary to this repository and its GitHub Actions build.

## Release workflow

The signed-release workflow is defined in
[`.github/workflows/release-sign.yml`](.github/workflows/release-sign.yml).

1. A release tag is pushed.
2. GitHub Actions publishes the application and uploads `PixelForge.exe` as a
   workflow artifact.
3. SignPath signs the application executable.
4. The signed application is used to build the portable ZIP and the Inno Setup
   installer.
5. The setup executable is uploaded and signed by SignPath.
6. The workflow verifies both Authenticode signatures and timestamps, then
   generates `SHA256SUMS.txt` from the signed assets.
7. A draft GitHub release is created for maintainer review.

Signing requests time out if they are not approved. A failed or rejected
request does not produce release assets.

## Version 1.0.0

PixelForge 1.0.0 was published before SignPath Foundation enrollment. Its
existing release assets are **unsigned**. They will not be silently replaced;
signed binaries will be published in a later version after enrollment is
approved.

## Privacy

PixelForge does not collect telemetry or upload media. See
[PRIVACY.md](PRIVACY.md) for the full privacy policy and the network behavior of
optional model downloads.
