param(
    [string]$Version = "2.0.0",
    [string]$PublishDir = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $root "artifacts\publish\PixelForge"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "artifacts\installer-msi"
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$wixproj = Join-Path $root "packaging\PixelForge.Msi.wixproj"
$icon = Join-Path $root "src\PixelForge.Presentation.WinUI\Assets\PixelForge.ico"

function Get-Sha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $sha.Dispose()
    }
}

if (-not (Test-Path (Join-Path $PublishDir "PixelForge.exe"))) {
    throw "Publish output does not contain PixelForge.exe: $PublishDir"
}
if (-not (Test-Path (Join-Path $PublishDir "cli\pixelforge.exe"))) {
    throw "Publish output does not contain cli\pixelforge.exe. Run build-release.ps1 -Stage publish first."
}
if (-not (Test-Path $icon)) {
    throw "Application icon not found: $icon"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
dotnet build $wixproj -c Release `
    -p:InstallerPlatform=x64 `
    -p:Version=$Version `
    -p:PublishDir=$PublishDir `
    -p:IconPath=$icon
if ($LASTEXITCODE -ne 0) {
    throw "WiX MSI build failed with exit code $LASTEXITCODE."
}

$msi = Get-ChildItem (Join-Path $root "packaging") -Recurse -Filter "*.msi" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $msi) {
    throw "WiX did not produce an MSI under packaging."
}

$destination = Join-Path $OutputDir "PixelForge-$Version-Setup.msi"
Copy-Item $msi.FullName $destination -Force
$hash = Get-Sha256 $destination
"MSI=$destination"
"SIZE_BYTES=$((Get-Item $destination).Length)"
"SHA256=$hash"
