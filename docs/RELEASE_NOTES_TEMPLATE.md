# PixelForge {{VERSION}}

PixelForge is a local Windows studio for converting, processing, upscaling, and OCR-ing media. It runs on your PC; no account or cloud upload is required.

## Downloads

- `PixelForge-Setup-{{VERSION}}-x64.exe`
- `PixelForge-Portable-{{VERSION}}-x64.zip`
- `SHA256SUMS.txt`

## Code signing

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org). The setup executable and the application executable inside the portable ZIP are Authenticode-signed and timestamped.

## Verification

Verify the downloaded files with:

```powershell
Get-FileHash .\PixelForge-Setup-{{VERSION}}-x64.exe -Algorithm SHA256
Get-FileHash .\PixelForge-Portable-{{VERSION}}-x64.zip -Algorithm SHA256
```

Compare the results with `SHA256SUMS.txt`. To inspect the Authenticode signature:

```powershell
Get-AuthenticodeSignature .\PixelForge-Setup-{{VERSION}}-x64.exe
```

## Links

- [README](https://github.com/JuggyBob/PixelForge/blob/main/README.md)
- [Code signing policy](https://github.com/JuggyBob/PixelForge/blob/main/CODE_SIGNING.md)
- [Privacy policy](https://github.com/JuggyBob/PixelForge/blob/main/PRIVACY.md)
- [Third-party notices](https://github.com/JuggyBob/PixelForge/blob/main/THIRD_PARTY_NOTICES.md)
