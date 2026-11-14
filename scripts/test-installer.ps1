param(
    [string]$Installer,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Installer)) {
    $Installer = Join-Path $root "artifacts\installer\PixelForge-1.0.0-Setup.exe"
}
$Installer = [System.IO.Path]::GetFullPath($Installer)
if (-not (Test-Path $Installer)) { throw "Installer not found: $Installer" }

$testRoot = Join-Path $root "artifacts\install-test"
$installDir = Join-Path $testRoot "PixelForge"
$dataDir = Join-Path $testRoot "appdata"
$installLog = Join-Path $testRoot "install.log"
$uninstallLog = Join-Path $testRoot "uninstall.log"
$report = Join-Path $root "artifacts\installer\install-test.txt"
$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\PixelForge.lnk"

if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

$results = [System.Collections.Generic.List[string]]::new()
function Check([string]$label, [scriptblock]$condition) {
    $ok = & $condition
    $results.Add("$(if ($ok) { 'PASS' } else { 'FAIL' })  $label")
    if (-not $ok) { throw "Installer verification failed: $label" }
    Write-Host "PASS  $label" -ForegroundColor Green
}

Write-Host "Installing to $installDir" -ForegroundColor Cyan
$installArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER",
    "/DIR=$installDir", "/LOG=$installLog"
)
$install = Start-Process -FilePath $Installer -ArgumentList $installArgs -Wait -PassThru
Check "Setup exited successfully" { $install.ExitCode -eq 0 }
Check "PixelForge.exe installed" { Test-Path (Join-Path $installDir "PixelForge.exe") }
Check "FFmpeg bundled" { Test-Path (Join-Path $installDir "ffmpeg\ffmpeg.exe") }
Check "FFprobe bundled" { Test-Path (Join-Path $installDir "ffmpeg\ffprobe.exe") }
Check "Pandoc bundled" { Test-Path (Join-Path $installDir "documents\pandoc\pandoc.exe") }
Check "Typst bundled" { Test-Path (Join-Path $installDir "documents\typst\typst.exe") }
Check "7-Zip bundled" { Test-Path (Join-Path $installDir "archives\7z.exe") }
Check "Zstandard bundled" { Test-Path (Join-Path $installDir "archives\zstd.exe") }
Check "RNNoise model bundled" { Test-Path (Join-Path $installDir "audio\rnnoise\std.rnnn") }
Check "u2netp model bundled" { Test-Path (Join-Path $installDir "models\segmentation\u2netp.onnx") }
Check "Third-party notices installed" { Test-Path (Join-Path $installDir "THIRD-PARTY-NOTICES.md") }
Check "JetBrains Mono installed" { Test-Path (Join-Path $installDir "Assets\Fonts\JetBrainsMono-Regular.ttf") }
Check "Uninstaller installed" { Test-Path (Join-Path $installDir "unins000.exe") }
Check "Start menu shortcut created" { Test-Path $shortcut }

Write-Host "Launching the installed application..." -ForegroundColor Cyan
$previousDataDir = $env:PIXELFORGE_DATA_DIR
$env:PIXELFORGE_DATA_DIR = $dataDir
$app = $null
try {
    $app = Start-Process -FilePath (Join-Path $installDir "PixelForge.exe") -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    $windowSeen = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        if ($app.HasExited) { break }
        if ($app.MainWindowTitle -eq "PixelForge") { $windowSeen = $true; break }
    }
    Check "Installed application launches" { $windowSeen }
}
finally {
    if ($app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { $app.Kill($true); $app.WaitForExit(5000) }
    }
    $env:PIXELFORGE_DATA_DIR = $previousDataDir
}

Write-Host "Uninstalling..." -ForegroundColor Cyan
$uninstaller = Join-Path $installDir "unins000.exe"
$uninstall = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$uninstallLog") -Wait -PassThru
Check "Uninstaller exited successfully" { $uninstall.ExitCode -eq 0 }

$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline -and (Test-Path (Join-Path $installDir "PixelForge.exe"))) {
    Start-Sleep -Milliseconds 500
}
Check "Application executable removed" { -not (Test-Path (Join-Path $installDir "PixelForge.exe")) }
Check "Start menu shortcut removed" { -not (Test-Path $shortcut) }

$header = @(
    "PixelForge installer verification",
    "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "Installer: $Installer",
    "Installer SHA-256: $((Get-FileHash $Installer -Algorithm SHA256).Hash)",
    "Install directory: $installDir",
    ""
)
$header + $results | Set-Content $report -Encoding utf8
Write-Host "Installer verification complete: $report" -ForegroundColor Cyan

if (-not $KeepArtifacts) {
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
