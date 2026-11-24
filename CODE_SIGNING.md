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

**Current status: no PixelForge release asset is signed.** PixelForge 1.0.0 and
2.0.0 were published unsigned. SignPath enrollment and the repository secret and
variables are not configured, so no SignPath request has been submitted and the
v2.0.0 assets are not Authenticode-signed. Verify SHA-256 checksums before
running any installer; Windows SmartScreen may show a warning.

The table below describes the intended coverage **for a future signed release**;
it does not apply to 1.0.0 or 2.0.0.

| Artifact | Signature in a future signed release |
|---|---|
| `PixelForge.exe` | Authenticode, timestamped |
| `PixelForge-Setup-<version>-x64.exe` | Authenticode, timestamped |
| `PixelForge.exe` inside the portable ZIP | Authenticode, timestamped |
| Portable ZIP itself | Not an Authenticode format; the contained executable is signed |
| MSI | Not covered by the current SignPath release workflow |

Third-party executables and libraries redistributed with PixelForge, such as
FFmpeg, Pandoc, Typst, 7-Zip, and Zstandard, remain under their upstream
publishers and are not signed with the PixelForge/SignPath certificate. The
Inno Setup uninstaller stub generated on the user's machine is not covered by
the setup executable's signature.

For a future signed release, the certificate will be issued to **SignPath
Foundation**, not to PixelForge. Windows will display SignPath Foundation as the
publisher. The signature will link the binary to this repository and its GitHub
Actions build.

## Release workflow (not used for 2.0.0)

The v2.0.0 EXE, MSI, and portable ZIP were built locally and published unsigned.
The workflow below is retained for the future first signed release only.

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

## Versions 1.0.0 and 2.0.0 (unsigned)

PixelForge 1.0.0 was published before SignPath Foundation enrollment and remains
unsigned. PixelForge 2.0.0 is also published unsigned: enrollment and the
required GitHub secret and variables are not configured, so no signing request
was made and no signed assets were uploaded. Existing release assets are not
replaced. A future version will be the first signed release after approval.

## Privacy

PixelForge does not collect telemetry or upload media. See
[PRIVACY.md](PRIVACY.md) for the full privacy policy and the network behavior of
optional model downloads.
