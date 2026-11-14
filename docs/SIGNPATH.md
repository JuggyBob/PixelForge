# SignPath Foundation setup

PixelForge uses [SignPath Foundation](https://signpath.org) for free
open-source code signing. SignPath Foundation issues the certificate in its own
name and keeps the private key in an HSM. **No PFX, certificate password, USB
token, or exportable private key is involved.**

The only credentials are:

| Item | Who handles it | Where it goes |
|---|---|---|
| SignPath account password and MFA | Maintainer | SignPath login only |
| SignPath API token | Maintainer | GitHub repository secret `SIGNPATH_API_TOKEN` |
| GitHub App authorization | Maintainer | GitHub repository installation |
| Signing-request approval | Maintainer | SignPath web UI, once per release |

The API token is a bearer credential. Do not commit it, print it, or paste it
into an issue. Rotate it if it is exposed.

## 1. Apply

Apply at <https://signpath.org/apply>. The form asks for project, repository,
download, privacy, reputation, maintainer, and build-system information.

PixelForge answers prepared for the application:

| Field | Value |
|---|---|
| Project Name | `PixelForge` |
| Repository URL | `https://github.com/JuggyBob/PixelForge` |
| Homepage URL | `https://github.com/JuggyBob/PixelForge` |
| Download URL | `https://github.com/JuggyBob/PixelForge/releases/latest` |
| Privacy Policy URL | `https://github.com/JuggyBob/PixelForge/blob/main/PRIVACY.md` |
| Wikipedia URL | Leave empty |
| Tagline | `A local Windows studio for converting, processing, upscaling, and OCR-ing media.` |
| Category | `Graphics` |
| Maintainer Type | `Individual maintainer(s)` |
| Build System | `GitHub Actions` |
| Primary Discovery Channel | `AI / LLM tools` |

SignPath reviews eligibility and reputation. Acceptance is not automatic. The
project must remain open source, maintained, documented, and released. A newly
published project with no external usage history may be asked for more evidence
of reputation.

## 2. Create the SignPath account

After approval, follow the invitation to create the SignPath account. Use the
maintainer's email address and enable MFA. This is the only account password
step; it is never stored in GitHub.

## 3. Install the SignPath GitHub App

Install <https://github.com/apps/signpath> for the `JuggyBob/PixelForge`
repository. SignPath uses the app to verify that the signing request came from
this repository and a GitHub Actions workflow.

## 4. Configure the SignPath project

In SignPath:

1. Add the **GitHub.com** trusted build system to the organization.
2. Create a project with slug `pixelforge`.
3. Link the GitHub trusted build system to the project.
4. Create a signing policy with slug `release`.
5. Require manual approval for the policy and assign the maintainer as approver.
6. Create two artifact configurations and upload the XML from this repository:

| SignPath slug | File | Purpose |
|---|---|---|
| `pixelforge-app` | [`packaging/signpath/app-artifact-config.xml`](../packaging/signpath/app-artifact-config.xml) | Signs `PixelForge.exe` |
| `pixelforge-installer` | [`packaging/signpath/installer-artifact-config.xml`](../packaging/signpath/installer-artifact-config.xml) | Signs `PixelForge-Setup.exe` |

The configurations enforce the PE product name, product version, and file
version before signing. If the binary metadata does not match the parameters
supplied by the workflow, SignPath rejects the request.

## 5. Create the API token

Create a SignPath API token for a user with submitter permission on the
`pixelforge` project and `release` policy. Store it as a GitHub Actions secret:

```powershell
gh secret set SIGNPATH_API_TOKEN --repo JuggyBob/PixelForge
```

Paste the token when prompted. Do not pass it as a command-line argument.

Add the non-secret configuration values as repository variables:

```powershell
gh variable set SIGNPATH_ORGANIZATION_ID --repo JuggyBob/PixelForge --body "<organization-id>"
gh variable set SIGNPATH_PROJECT_SLUG --repo JuggyBob/PixelForge --body "pixelforge"
gh variable set SIGNPATH_SIGNING_POLICY_SLUG --repo JuggyBob/PixelForge --body "release"
gh variable set SIGNPATH_APP_CONFIG_SLUG --repo JuggyBob/PixelForge --body "pixelforge-app"
gh variable set SIGNPATH_INSTALLER_CONFIG_SLUG --repo JuggyBob/PixelForge --body "pixelforge-installer"
```

The organization ID is shown in the SignPath organization settings. The slugs
must match the project, policy, and artifact configurations exactly.

## 6. Build a signed release

Do not replace the existing unsigned `v1.0.0` assets. Create a new version, for
example `v1.0.1`:

```powershell
git tag -a v1.0.1 -m "PixelForge 1.0.1"
git push origin v1.0.1
```

The [`release-sign`](../.github/workflows/release-sign.yml) workflow:

1. Builds on a GitHub-hosted Windows runner.
2. Uploads the unsigned application to GitHub as a workflow artifact.
3. Submits the application to SignPath.
4. Waits for manual approval in SignPath.
5. Rebuilds the portable ZIP and installer around the signed application.
6. Submits the setup executable to SignPath.
7. Verifies both Authenticode signatures and timestamps.
8. Generates `SHA256SUMS.txt` from the signed files.
9. Creates a **draft** GitHub release.

Approve the two signing requests in SignPath when they appear. The workflow has
a one-hour wait limit per request. If the request times out or is rejected, no
release assets are uploaded.

After the workflow completes, download the draft assets, verify them once more,
and publish the release manually. Do not let an unreviewed workflow publish a
stable release automatically.

## 7. Verify locally

```powershell
Get-AuthenticodeSignature .\PixelForge-Setup-1.0.1-x64.exe |
  Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate

Get-FileHash .\PixelForge-Setup-1.0.1-x64.exe -Algorithm SHA256
Get-FileHash .\PixelForge-Portable-1.0.1-x64.zip -Algorithm SHA256
```

The signer subject should contain `SignPath Foundation`, `Status` should be
`Valid`, and `TimeStamperCertificate` should be present.

## Troubleshooting

| Symptom | Check |
|---|---|
| Missing configuration error | Set the secret and all five repository variables. |
| Signing request rejected | Check the artifact configuration path; the app ZIP must contain `PixelForge.exe` at its root, and the installer ZIP must contain `PixelForge-Setup.exe`. |
| Metadata mismatch | The PE product name/version/file version must match the parameters in the workflow. Run `(Get-Item .\PixelForge.exe).VersionInfo`. |
| Workflow waits indefinitely | Approve the request in SignPath. The default wait is one hour. |
| Signature status is not `Valid` | Check the certificate chain and timestamp. Do not publish an asset with an invalid signature. |
| Release is already published | The workflow refuses to overwrite published assets. Create a new version. |

## Security notes

- SignPath Foundation holds the private key; it is never exported to GitHub.
- The certificate is issued to SignPath Foundation, not to PixelForge.
- The API token is a bearer token. Store it only in GitHub Actions secrets.
- Signing requests require manual approval.
- Builds must run on GitHub-hosted runners for the open-source policy.
- The workflow refuses to overwrite a published release.
