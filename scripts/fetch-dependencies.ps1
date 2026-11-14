param(
    [switch]$SkipModels,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$ffmpegRoot = Join-Path $root "third_party\ffmpeg"
$ffmpegZip = Join-Path $ffmpegRoot "ffmpeg-lgpl-shared.zip"
$ffmpegBin = Join-Path $ffmpegRoot "bin"
$fontZip = Join-Path $root "third_party\fonts\JetBrainsMono-2.304.zip"
$fontTarget = Join-Path $root "third_party\fonts\JetBrainsMono"
$pandocRoot = Join-Path $root "third_party\documents\pandoc"
$typstRoot = Join-Path $root "third_party\documents\typst"
$modelTarget = Join-Path $root "third_party\models\downloads"

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Download([string]$url, [string]$destination, [string]$sha256) {
    Write-Host "Downloading $url" -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $destination -TimeoutSec 600
    if ($sha256) {
        $actual = (Get-FileHash $destination -Algorithm SHA256).Hash
        if ($actual -ne $sha256.ToUpperInvariant()) {
            throw "SHA-256 mismatch for $destination. Expected $sha256, got $actual."
        }
    }
}

if ($Force -or -not (Test-Path (Join-Path $ffmpegBin "ffmpeg.exe"))) {
    if ($Force -or -not (Test-Path $ffmpegZip)) {
        Download "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip" `
            $ffmpegZip "2561C6F8216E510B236BCA914E5EFB5445E61167C72213251E814DD6FFBBE221"
    } else {
        $actual = (Get-FileHash $ffmpegZip -Algorithm SHA256).Hash
        if ($actual -ne "2561C6F8216E510B236BCA914E5EFB5445E61167C72213251E814DD6FFBBE221") {
            Write-Warning "The existing FFmpeg ZIP has a different hash than the verified development build. It will be used as-is."
        }
    }

    $temp = Join-Path $env:TEMP ("pixelforge-ffmpeg-" + [guid]::NewGuid().ToString("N"))
    Expand-Archive $ffmpegZip -DestinationPath $temp -Force
    $sourceRoot = Get-ChildItem $temp -Directory | Where-Object { $_.Name -like "ffmpeg-*-win64-lgpl-shared*" } | Select-Object -First 1
    if (-not $sourceRoot) { throw "Unexpected FFmpeg archive layout." }
    New-Item -ItemType Directory -Path $ffmpegBin -Force | Out-Null
    Copy-Item (Join-Path $sourceRoot.FullName "bin\*.exe") $ffmpegBin -Force
    Copy-Item (Join-Path $sourceRoot.FullName "bin\*.dll") $ffmpegBin -Force
    $license = Join-Path $sourceRoot.FullName "LICENSE.txt"
    if (Test-Path $license) { Copy-Item $license (Join-Path $ffmpegRoot "LICENSE.txt") -Force }
    Remove-Item $temp -Recurse -Force
    Write-Host "FFmpeg installed to $ffmpegBin" -ForegroundColor Green
}

if ($Force -or -not (Test-Path (Join-Path $fontTarget "JetBrainsMono-Regular.ttf"))) {
    if ($Force -or -not (Test-Path $fontZip)) {
        Download "https://github.com/JetBrains/JetBrainsMono/releases/download/v2.304/JetBrainsMono-2.304.zip" `
            $fontZip "6F6376C6ED2960EA8A963CD7387EC9D76E3F629125BC33D1FDCD7EB7012F7BBF"
    }
    $temp = Join-Path $env:TEMP ("pixelforge-fonts-" + [guid]::NewGuid().ToString("N"))
    Expand-Archive $fontZip -DestinationPath $temp -Force
    New-Item -ItemType Directory -Path $fontTarget -Force | Out-Null
    Get-ChildItem $temp -Recurse -Filter "JetBrainsMono-*.ttf" | Copy-Item -Destination $fontTarget -Force
    $ofl = Get-ChildItem $temp -Recurse -Filter "OFL.txt" | Select-Object -First 1
    if ($ofl) { Copy-Item $ofl.FullName (Join-Path $fontTarget "OFL.txt") -Force }
    Remove-Item $temp -Recurse -Force
    Write-Host "JetBrains Mono installed to $fontTarget" -ForegroundColor Green
}

if ($Force -or -not (Test-Path (Join-Path $pandocRoot "pandoc.exe"))) {
    $pandocZip = Join-Path $env:TEMP ("pandoc-3.13-" + [guid]::NewGuid().ToString("N") + ".zip")
    Download "https://github.com/jgm/pandoc/releases/download/3.13/pandoc-3.13-windows-x86_64.zip" $pandocZip
    $temp = Join-Path $env:TEMP ("pixelforge-pandoc-" + [guid]::NewGuid().ToString("N"))
    Expand-Archive $pandocZip -DestinationPath $temp -Force
    New-Item -ItemType Directory -Path $pandocRoot -Force | Out-Null
    $pandoc = Get-ChildItem $temp -Recurse -Filter "pandoc.exe" | Select-Object -First 1
    if (-not $pandoc) { throw "The Pandoc archive did not contain pandoc.exe." }
    Copy-Item $pandoc.FullName (Join-Path $pandocRoot "pandoc.exe") -Force
    $actual = (Get-FileHash (Join-Path $pandocRoot "pandoc.exe") -Algorithm SHA256).Hash
    if ($actual -ne "7AB6244548BA15494FAB956230050516FE8F57C36EC2DFB50133719AD3B5A791") {
        throw "Pandoc executable SHA-256 mismatch: $actual"
    }
    $copying = Get-ChildItem $temp -Recurse -Filter "COPYING.rtf" | Select-Object -First 1
    if ($copying) { Copy-Item $copying.FullName (Join-Path $pandocRoot "COPYING.rtf") -Force }
    $copyright = Get-ChildItem $temp -Recurse -Filter "COPYRIGHT.txt" | Select-Object -First 1
    if ($copyright) { Copy-Item $copyright.FullName (Join-Path $pandocRoot "COPYRIGHT.txt") -Force }
    Remove-Item $pandocZip -Force
    Remove-Item $temp -Recurse -Force
    Write-Host "Pandoc installed to $pandocRoot" -ForegroundColor Green
}

if ($Force -or -not (Test-Path (Join-Path $typstRoot "typst.exe"))) {
    $typstZip = Join-Path $env:TEMP ("typst-0.15.1-" + [guid]::NewGuid().ToString("N") + ".zip")
    Download "https://github.com/typst/typst/releases/download/v0.15.1/typst-x86_64-pc-windows-msvc.zip" $typstZip
    $temp = Join-Path $env:TEMP ("pixelforge-typst-" + [guid]::NewGuid().ToString("N"))
    Expand-Archive $typstZip -DestinationPath $temp -Force
    New-Item -ItemType Directory -Path $typstRoot -Force | Out-Null
    $typst = Get-ChildItem $temp -Recurse -Filter "typst.exe" | Select-Object -First 1
    if (-not $typst) { throw "The Typst archive did not contain typst.exe." }
    Copy-Item $typst.FullName (Join-Path $typstRoot "typst.exe") -Force
    $actual = (Get-FileHash (Join-Path $typstRoot "typst.exe") -Algorithm SHA256).Hash
    if ($actual -ne "081217A463ADB006F8894B44227FE4B9C9E91FC85F5463D5948E8370DB9BB31E") {
        throw "Typst executable SHA-256 mismatch: $actual"
    }
    $license = Get-ChildItem $temp -Recurse -Filter "LICENSE" | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $typstRoot "LICENSE") -Force }
    $notice = Get-ChildItem $temp -Recurse -Filter "NOTICE" | Select-Object -First 1
    if ($notice) { Copy-Item $notice.FullName (Join-Path $typstRoot "NOTICE") -Force }
    Remove-Item $typstZip -Force
    Remove-Item $temp -Recurse -Force
    Write-Host "Typst installed to $typstRoot" -ForegroundColor Green
}

if (-not $SkipModels) {
    New-Item -ItemType Directory -Path $modelTarget -Force | Out-Null
    $models = @(
        @{ Name = "real_esrgan_x4.onnx"; Url = "https://huggingface.co/SceneWorks/real-esrgan-onnx/resolve/main/real_esrgan_x4.onnx"; Sha = "5C586662929CBC686C1A5C38D9C060DBDB4EA5863A1F7672B8C0761E6B89C033" },
        @{ Name = "real_esrgan_x2.onnx"; Url = "https://huggingface.co/SceneWorks/real-esrgan-onnx/resolve/main/real_esrgan_x2.onnx"; Sha = "7115BA92E8A1BFA63D68558EF006EF3D91273A068D321B1439F8BB1C9179002C" },
        @{ Name = "realesrgan_anime6b.onnx"; Url = "https://huggingface.co/RekluzLabs/realesrgan_anime6b.onnx/resolve/main/realesrgan_anime6b.onnx"; Sha = "45BD54934AEABE8DF744C8FDACB9E8846C9B55CB4E60C499DB77405D1625A667" },
        @{ Name = "realesr-general-x4v3.onnx"; Url = "https://huggingface.co/Heliosoph/realesrgan-onnx/resolve/main/realesr-general-x4v3.onnx"; Sha = "09B757ACCD747D7E423C1D352B3E8F23E77CC5742D04BAE958D4EB8082B76FA4" },
        @{ Name = "realesr-animevideov3.onnx"; Url = "https://huggingface.co/skillsafe-ai/realesr-animevideov3/resolve/main/model.onnx"; Sha = "78BAA685A1A92CAC6E14AB2AF2A8AD0EF56FA48629563B1414E3A7ACC54D86E2" },
        @{ Name = "rife_v425lite.onnx"; Url = "https://huggingface.co/t9749/rife-4.25-lite-onnx/resolve/main/rife_v425lite.onnx"; Sha = "702802D0862AB78F2D72133C6BC3768E7E89B81756F7CD51D1055A2FE3D06577" }
    )
    foreach ($model in $models) {
        $destination = Join-Path $modelTarget $model.Name
        if (-not $Force -and (Test-Path $destination)) {
            $actual = (Get-FileHash $destination -Algorithm SHA256).Hash
            if ($actual -eq $model.Sha) { continue }
            throw "Existing model hash mismatch: $destination"
        }
        Download $model.Url $destination $model.Sha
        Write-Host "Installed $($model.Name)" -ForegroundColor Green
    }
}

Write-Host "Dependencies are ready." -ForegroundColor Cyan
