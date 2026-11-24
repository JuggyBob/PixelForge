param(
    [string]$Version = "2.0.0",
    [ValidateSet("all", "publish", "package")]
    [string]$Stage = "all",
    [string]$SignedAppPath,
    [switch]$SkipInstaller,
    [switch]$SkipPortable
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\PixelForge.Presentation.WinUI\PixelForge.Presentation.WinUI.csproj"
$cliProject = Join-Path $root "src\PixelForge.Cli\PixelForge.Cli.csproj"
$publish = Join-Path $root "artifacts\publish\PixelForge"
$cliPublish = Join-Path $root "artifacts\publish\cli"
$installerDir = Join-Path $root "artifacts\installer"
$portableDir = Join-Path $root "artifacts\portable"
$iscc = Join-Path $root "tools\InnoSetup\ISCC.exe"

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

function Assert-PublishOutput {
    param([string]$PublishPath)

    $required = @(
        "PixelForge.exe",
        "cli\pixelforge.exe",
        "cli\ppocrv6_small_dict.txt",
        "PixelForge.dll",
        "PixelForge.pri",
        "App.xbf",
        "Views\ShellPage.xbf",
        "Views\ConverterPage.xbf",
        "Views\PipelinesPage.xbf",
        "Views\ToolsPage.xbf",
        "Views\WatchFoldersPage.xbf",
        "Controls\FormatPicker.xbf",
        "Controls\PipelineCanvas.xbf",
        "Controls\PipelineNodeControl.xbf",
        "Themes\Styles.xbf",
        "THIRD-PARTY-NOTICES.md",
        "LICENSE.txt",
        "ffmpeg\ffmpeg.exe",
        "ffmpeg\ffprobe.exe",
        "ffmpeg\LICENSE.txt",
        "documents\pandoc\pandoc.exe",
        "documents\typst\typst.exe",
        "archives\7z.exe",
        "archives\zstd.exe",
        "audio\rnnoise\std.rnnn",
        "models\segmentation\u2netp.onnx",
        "Assets\Fonts\JetBrainsMono-Regular.ttf",
        "licenses\JetBrainsMono-OFL.txt"
    )
    foreach ($relative in $required) {
        if (-not (Test-Path (Join-Path $PublishPath $relative))) {
            throw "Publish output is missing required file: $relative"
        }
    }
}

Write-Host "PixelForge release $Version (stage: $Stage)" -ForegroundColor Cyan
Write-Host "Repository: $root"

$running = Get-Process -Name PixelForge -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running PixelForge process(es) so the build output can be replaced." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

if ($Stage -in @("all", "publish")) {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $publish) -Force | Out-Null

    Write-Host "Publishing self-contained x64 build..." -ForegroundColor Cyan
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 `
        -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
        -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing headless CLI..." -ForegroundColor Cyan
    if (Test-Path $cliPublish) { Remove-Item $cliPublish -Recurse -Force }
    dotnet publish $cliProject -c Release -r win-x64 --self-contained true -p:Platform=x64 `
        -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
        -o $cliPublish
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE." }
    $cliExe = Join-Path $cliPublish "pixelforge.exe"
    if (-not (Test-Path $cliExe)) { throw "CLI publish did not produce pixelforge.exe." }
    $cliDir = Join-Path $publish "cli"
    New-Item -ItemType Directory -Force -Path $cliDir | Out-Null
    Get-ChildItem $cliPublish -File | Where-Object { $_.Extension -notin '.pdb', '.lib' } |
        Copy-Item -Destination $cliDir -Force
    $cliResources = Join-Path $cliPublish "Resources"
    if (Test-Path $cliResources) {
        Get-ChildItem $cliResources -File | Copy-Item -Destination $cliDir -Force
    }
    Remove-Item $cliPublish -Recurse -Force

    # WinUI-generated XAML resources. The project has an AfterTargets=Publish copy as
    # well; repeating it here makes packaging resilient to SDK publish changes.
    $releaseBin = Join-Path $root "src\PixelForge.Presentation.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64"
    if (Test-Path (Join-Path $releaseBin "PixelForge.pri")) {
        Copy-Item (Join-Path $releaseBin "PixelForge.pri") (Join-Path $publish "PixelForge.pri") -Force
    }
    Get-ChildItem $releaseBin -Recurse -Filter *.xbf | ForEach-Object {
        $relative = $_.FullName.Substring($releaseBin.Length + 1)
        $destination = Join-Path $publish $relative
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item $_.FullName $destination -Force
    }

    # Debug symbols and import libraries are not useful in the installed application.
    Get-ChildItem $publish -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.lib' } | Remove-Item -Force -ErrorAction SilentlyContinue

    Copy-Item (Join-Path $root "LICENSE") (Join-Path $publish "LICENSE.txt") -Force
    Copy-Item (Join-Path $root "NOTICE") (Join-Path $publish "NOTICE.txt") -Force
    Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") (Join-Path $publish "THIRD_PARTY_NOTICES.md") -Force
    Copy-Item (Join-Path $root "docs\THIRD-PARTY-NOTICES.md") (Join-Path $publish "THIRD-PARTY-NOTICES.md") -Force
    if (Test-Path (Join-Path $root "licenses")) {
        New-Item -ItemType Directory -Path (Join-Path $publish "licenses") -Force | Out-Null
        Copy-Item (Join-Path $root "licenses\*") (Join-Path $publish "licenses") -Recurse -Force
    }

    Assert-PublishOutput $publish

    $files = Get-ChildItem $publish -Recurse -File
    $sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host ("Published {0:N0} files ({1} MB)." -f $files.Count, $sizeMb) -ForegroundColor Green
} else {
    Write-Host "Skipping publish stage; using existing publish output." -ForegroundColor Yellow
}

if ($Stage -in @("all", "package")) {
    foreach ($dir in @($installerDir, $portableDir)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
    New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
    New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

    if (-not (Test-Path $publish)) {
        throw "Publish output not found at $publish. Run the publish stage first."
    }

    if ($SignedAppPath) {
        if (-not (Test-Path $SignedAppPath)) {
            throw "Signed application not found at $SignedAppPath."
        }
        Copy-Item $SignedAppPath (Join-Path $publish "PixelForge.exe") -Force
        $signature = Get-AuthenticodeSignature (Join-Path $publish "PixelForge.exe")
        if ($signature.Status -ne "Valid") {
            throw "Signed application signature is not valid: $($signature.Status) $($signature.StatusMessage)"
        }
        if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch "SignPath Foundation") {
            throw "Signed application has an unexpected signer: $($signature.SignerCertificate.Subject)"
        }
        Write-Host "Using signed application: $SignedAppPath" -ForegroundColor Green
    }

    Assert-PublishOutput $publish

    $artifacts = @()
    $installerPath = Join-Path $installerDir "PixelForge-$Version-Setup.exe"

    if (-not $SkipPortable) {
        Write-Host "Creating portable ZIP..." -ForegroundColor Cyan
        $zip = Join-Path $portableDir "PixelForge-$Version-portable.zip"
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        $artifacts += $zip
        Write-Host ("Portable ZIP: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
    }

    if (-not $SkipInstaller) {
        if (-not (Test-Path $iscc)) {
            $isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if ($isccCommand) { $iscc = $isccCommand.Source }
        }
        if (-not (Test-Path $iscc)) {
            throw "Inno Setup 7 compiler not found. Install it or place ISCC.exe under tools\InnoSetup."
        }
        Write-Host "Building per-user installer..." -ForegroundColor Cyan
        & $iscc "/DAppVersion=$Version" (Join-Path $root "packaging\PixelForge.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path $installerPath)) { throw "Installer was not produced at $installerPath." }
        $artifacts += $installerPath
        Write-Host ("Installer: {0} ({1:N1} MB)" -f $installerPath, ((Get-Item $installerPath).Length / 1MB)) -ForegroundColor Green
    }

    if ($artifacts.Count -gt 0) {
        $checksums = Join-Path $root "artifacts\SHA256SUMS.txt"
        $lines = foreach ($file in $artifacts) {
            $hash = Get-Sha256 $file
            "$hash  $([System.IO.Path]::GetFileName($file))"
        }
        $lines | Set-Content $checksums -Encoding ascii
        Write-Host "Checksums written to $checksums" -ForegroundColor Green
    } else {
        Write-Warning "No packages were requested; no checksums were written."
    }

    Write-Host "Release packaging complete." -ForegroundColor Cyan
} else {
    Write-Host "Publish stage complete; no packages created." -ForegroundColor Cyan
}
