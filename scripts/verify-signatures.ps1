param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,
    [string]$ExpectedSigner = "SignPath Foundation"
)

$ErrorActionPreference = "Stop"
$failures = @()

foreach ($file in $Path) {
    if (-not (Test-Path $file)) {
        $failures += "Missing file: $file"
        continue
    }

    $signature = Get-AuthenticodeSignature -FilePath $file
    $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
    $timestamp = if ($signature.TimeStamperCertificate) { $signature.TimeStamperCertificate.Subject } else { "" }

    Write-Host $file
    Write-Host "  Status:    $($signature.Status)"
    Write-Host "  Signer:    $subject"
    Write-Host "  Timestamp: $timestamp"

    if ($signature.Status -ne "Valid") {
        $failures += "$file signature status is $($signature.Status): $($signature.StatusMessage)"
    }
    if (-not $signature.SignerCertificate -or $subject -notmatch [regex]::Escape($ExpectedSigner)) {
        $failures += "$file signer does not contain '$ExpectedSigner'."
    }
    if (-not $signature.TimeStamperCertificate) {
        $failures += "$file has no timestamp countersignature."
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    throw "Signature verification failed."
}

Write-Host "All signatures are valid and timestamped." -ForegroundColor Green
