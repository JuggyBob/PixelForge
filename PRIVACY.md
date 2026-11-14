# Privacy policy

PixelForge is a local Windows desktop application. It does not require an
account and does not operate a cloud service.

## Data collection

PixelForge does not include telemetry, analytics, advertising, or crash
reporting that transmits data. It does not upload media, documents, filenames,
history, settings, or logs.

**This program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or operating
it.**

## Network access

Network access occurs only when the user explicitly requests it:

- **Optional AI models and runtimes.** The shared AI Models panel downloads model
  weights and runtime packages from their upstream hosts, currently Hugging
  Face and GitHub. The request goes directly to the host and may expose the
  user's IP address and user agent to that service under its privacy policy.
  Downloads are SHA-256 verified before installation.
- **Links opened by the user.** Documentation, release, and license links open
  in the user's browser.

Advanced OCR can run a local llama.cpp server. It binds to loopback
(`127.0.0.1`) for the duration of the job and is not exposed to the network.

Normal conversion, processing, upscaling, OCR, queue, history, watch-folder, and
settings operations run entirely on the local machine.

## Local data

PixelForge stores settings, queue state, history, logs, caches, and downloaded
models under `%LOCALAPPDATA%\PixelForge` by default. Set `PIXELFORGE_DATA_DIR` to
use a different profile directory. Output files are written only to the
destinations selected by the user.

The application does not keep copies of user media in its history database.

## Third-party components and services

The application redistributes local third-party engines. They run on the user's
machine and do not receive data from PixelForge beyond the files the user
processes. Their licenses and source links are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Optional downloads are hosted by third parties with their own privacy policies:

- [Hugging Face privacy policy](https://huggingface.co/privacy)
- [GitHub privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-privacy-statement)

## Code signing

Release signing is provided by SignPath.io and SignPath Foundation. SignPath
receives the build artifact and GitHub workflow metadata required to verify its
origin. It does not receive user media or application data. See
[CODE_SIGNING.md](CODE_SIGNING.md).

## Security reports

Do not include personal data, private media, credentials, or unredacted logs in
a public issue. See [SECURITY.md](SECURITY.md) for private vulnerability
reporting.
