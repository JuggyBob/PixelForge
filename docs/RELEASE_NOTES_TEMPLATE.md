# PixelForge {{VERSION}}

PixelForge is a local Windows studio for converting, processing, upscaling, and OCR-ing media. It runs on your PC; no account or cloud upload is required.

## Downloads

- `PixelForge-Setup-{{VERSION}}-x64.exe` (Inno Setup installer, per user)
- `PixelForge-Setup-{{VERSION}}-x64.msi` (WiX MSI installer, per user; alternative to the EXE)
- `PixelForge-Portable-{{VERSION}}-x64.zip`
- `SHA256SUMS.txt`

## Code signing

**These assets are unsigned.** PixelForge 1.0.0 and v{{VERSION}} are published unsigned; SignPath signing is not yet configured. Windows SmartScreen may show a warning. Verify the checksums below before running an installer. See [CODE_SIGNING.md](https://github.com/JuggyBob/PixelForge/blob/main/CODE_SIGNING.md).

## Verification

Verify all three downloaded files:

```powershell
Get-FileHash .\PixelForge-Setup-{{VERSION}}-x64.exe -Algorithm SHA256
Get-FileHash .\PixelForge-Setup-{{VERSION}}-x64.msi -Algorithm SHA256
Get-FileHash .\PixelForge-Portable-{{VERSION}}-x64.zip -Algorithm SHA256
```

Compare the results with `SHA256SUMS.txt`. These assets are not signed, so `Get-AuthenticodeSignature` reports `NotSigned`, which is expected for this release.

## Links

- [README](https://github.com/JuggyBob/PixelForge/blob/main/README.md)
- [Changelog](https://github.com/JuggyBob/PixelForge/blob/main/CHANGELOG.md)
- [Code signing policy](https://github.com/JuggyBob/PixelForge/blob/main/CODE_SIGNING.md)
- [Security policy](https://github.com/JuggyBob/PixelForge/blob/main/SECURITY.md)
- [Privacy policy](https://github.com/JuggyBob/PixelForge/blob/main/PRIVACY.md)
- [Third-party notices](https://github.com/JuggyBob/PixelForge/blob/main/THIRD_PARTY_NOTICES.md)
- [Build and test](https://github.com/JuggyBob/PixelForge/blob/main/docs/BUILDING.md)
- [Update checking](https://github.com/JuggyBob/PixelForge/blob/main/docs/UPDATES.md)
