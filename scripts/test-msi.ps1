param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,
    [string]$TestRoot = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    $TestRoot = Join-Path $root "artifacts\msi-test"
}
$TestRoot = [System.IO.Path]::GetFullPath($TestRoot)
$installDir = Join-Path $TestRoot "Pixel Forge MSI With Spaces"
$logDir = Join-Path $TestRoot "logs"
$dataDir = Join-Path $TestRoot "appdata"
$failures = New-Object System.Collections.Generic.List[string]

function Pass([string]$message) { Write-Host "PASS  $message" -ForegroundColor Green }
function Fail([string]$message) { Write-Host "FAIL  $message" -ForegroundColor Red; $failures.Add($message) }
function Check([bool]$condition, [string]$message) { if ($condition) { Pass $message } else { Fail $message } }
function Test-RegKey([string]$key) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    & reg.exe query $key *> $null
    $exists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $old
    return $exists
}

if (-not (Test-Path $MsiPath)) { throw "MSI not found: $MsiPath" }
if (Test-Path $TestRoot) { Remove-Item $TestRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $installDir, $logDir, $dataDir | Out-Null

$msi = [System.IO.Path]::GetFullPath($MsiPath)
$installLog = Join-Path $logDir "install.log"
$uninstallLog = Join-Path $logDir "uninstall.log"

Write-Host "Installing $msi to $installDir" -ForegroundColor Cyan
$install = Start-Process msiexec.exe -ArgumentList @(
    "/i", "`"$msi`"", "/qn", "/L*v", "`"$installLog`"",
    "INSTALLFOLDER=`"$installDir`""
) -Wait -PassThru
Check ($install.ExitCode -eq 0) "MSI install exited successfully ($($install.ExitCode))"
Check (Test-Path (Join-Path $installDir "PixelForge.exe")) "GUI executable installed"
Check (Test-Path (Join-Path $installDir "cli\pixelforge.exe")) "CLI executable installed"
Check (Test-Path (Join-Path $installDir "cli\ppocrv6_small_dict.txt")) "CLI OCR dictionary installed"
Check (Test-Path (Join-Path $installDir "ffmpeg\ffmpeg.exe")) "FFmpeg bundled"
Check (Test-Path (Join-Path $installDir "THIRD-PARTY-NOTICES.md")) "Third-party notices installed"

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\PixelForge\PixelForge.lnk"
Check (Test-Path $startMenu) "Start menu shortcut created"

$arp = @(Get-ChildItem @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall") -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -eq "PixelForge" })
Check ($arp.Count -ge 1) "ARP entry created"

$perUserProduct = Get-ChildItem "HKCU:\Software\Microsoft\Installer\Products" -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.ProductName -eq "PixelForge" } |
    Select-Object -First 1
Check ($null -ne $perUserProduct) "Per-user product registration created"

Check (Test-RegKey "HKCU\Software\Classes\SystemFileAssociations\.jpg\shell\PixelForge") "Explorer image verb registered"
Check (Test-RegKey "HKCU\Software\Classes\SystemFileAssociations\.heic\shell\PixelForge") "Explorer HEIC image verb registered"
Check (Test-Path "HKCU:\Software\Classes\Software\PixelForge\ShellMenu\Images\Shell\UpscaleImage") "Upscale image verb registered"
Check (Test-RegKey "HKCU\Software\Classes\SystemFileAssociations\.mp3\shell\PixelForge") "Explorer audio verb registered"
Check (Test-RegKey "HKCU\Software\Classes\Directory\shell\PixelForgeFolder") "Explorer folder verb registered"

$env:PIXELFORGE_DATA_DIR = $dataDir
try {
    $version = & (Join-Path $installDir "cli\pixelforge.exe") --version 2>&1
    Check ($LASTEXITCODE -eq 0 -and $version -match '1\.1') "CLI runs and reports version: $version"

    $cliOut = Join-Path $TestRoot "cli-out"
    $sample = Join-Path $root "testdata\fixtures\photo-exif-orientation.jpg"
    $conversion = & (Join-Path $installDir "cli\pixelforge.exe") convert images `
        --input $sample --format png --output-dir $cliOut --json 2>&1
    $converted = Join-Path $cliOut "photo-exif-orientation.png"
    Check ($LASTEXITCODE -eq 0 -and (Test-Path $converted)) "Installed CLI performs a real conversion"
} finally {
    Remove-Item Env:PIXELFORGE_DATA_DIR -ErrorAction SilentlyContinue
}

$realDataDir = Join-Path $env:LOCALAPPDATA "PixelForge"
$process = $null
$previousDataDir = $env:PIXELFORGE_DATA_DIR
$env:PIXELFORGE_DATA_DIR = $dataDir
try {
    $process = Start-Process -FilePath (Join-Path $installDir "PixelForge.exe") -WorkingDirectory $installDir -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    $windowSeen = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { break }
        if ($process.MainWindowTitle -eq "PixelForge") { $windowSeen = $true; break }
    }
    Check $windowSeen "Installed application launched"
} finally {
    if ($process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
    }
    $env:PIXELFORGE_DATA_DIR = $previousDataDir
}

$dataBefore = Test-Path $realDataDir
$uninstall = Start-Process msiexec.exe -ArgumentList @(
    "/x", "`"$msi`"", "/qn", "/L*v", "`"$uninstallLog`""
) -Wait -PassThru
Check ($uninstall.ExitCode -eq 0) "MSI uninstall exited successfully ($($uninstall.ExitCode))"
Check (-not (Test-Path (Join-Path $installDir "PixelForge.exe"))) "GUI executable removed"
Check (-not (Test-Path $startMenu)) "Start menu shortcut removed"
Check (-not (Test-RegKey "HKCU\Software\Classes\SystemFileAssociations\.jpg\shell\PixelForge")) "Explorer verb removed"
Check (-not (Test-RegKey "HKCU\Software\Classes\SystemFileAssociations\.heic\shell\PixelForge")) "Explorer HEIC verb removed"
Check (-not (Test-RegKey "HKCU\Software\Classes\Directory\shell\PixelForgeFolder")) "Explorer folder verb removed"
$arpAfter = @(Get-ChildItem @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall") -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -eq "PixelForge" })
Check ($arpAfter.Count -eq 0) "ARP entry removed"
$perUserAfter = Get-ChildItem "HKCU:\Software\Microsoft\Installer\Products" -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.ProductName -eq "PixelForge" } |
    Select-Object -First 1
Check ($null -eq $perUserAfter) "Per-user product registration removed"
Check ((Test-Path $realDataDir) -eq $dataBefore) "User data directory was not removed by uninstall"

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAILED: $_" -ForegroundColor Red }
    throw "MSI verification failed with $($failures.Count) issue(s). Logs: $logDir"
}

Write-Host "MSI verification complete. Logs: $logDir" -ForegroundColor Green
