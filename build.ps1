$ErrorActionPreference = 'Stop'

Set-Location -LiteralPath $PSScriptRoot
if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK (x64) before running this script.'
}
$sdkVersion = & dotnet.exe --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notlike '8.*') {
    throw 'The .NET 8 SDK (x64) is required. See global.json.'
}

$outputRoot = Join-Path $PSScriptRoot 'build'
$workRoot = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N'))
$cacheRoot = Join-Path $PSScriptRoot '.runtime'
New-Item -ItemType Directory -Path $workRoot, $cacheRoot -Force | Out-Null
$previousRollForward = $env:DOTNET_ROLL_FORWARD

try {
    $ffmpegVersion = '2026-10-29-git-31ba826726'
    $ffmpegPackage = "ffmpeg-${ffmpegVersion}-essentials_build"
    $ffmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$ffmpegVersion/$ffmpegPackage.7z"
    $ffmpegHash = '94c9d4eae0817f4c2cc8c78fe2d001db347ce4a433a52f1d9312ff6a886451f8'
    $archive = Join-Path $cacheRoot "$ffmpegPackage.7z"
    if (!(Test-Path -LiteralPath $archive) -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $ffmpegHash) {
        Write-Host 'Downloading FFmpeg...'
        $download = Join-Path $workRoot "$ffmpegPackage.7z"
        $ffmpegAsset = 'https://api.github.com/repos/GyanD/codexffmpeg/releases/assets/688150779'
        & curl.exe --fail --location --silent --show-error --connect-timeout 15 --retry 2 `
            --header 'Accept: application/octet-stream' --output $download $ffmpegAsset
        if ($LASTEXITCODE -ne 0) { throw 'Could not download FFmpeg.' }
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $ffmpegHash) {
            throw 'The FFmpeg archive did not match the expected SHA-256.'
        }
        Move-Item -LiteralPath $download -Destination $archive -Force
    }

    & tar.exe -xf $archive -C $workRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract FFmpeg.' }
    $ffmpegRoot = Join-Path $workRoot $ffmpegPackage
    @{
        url = $ffmpegUrl
        sha256 = $ffmpegHash
        source = 'https://github.com/FFmpeg/FFmpeg/tree/31ba826726'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ffmpegRoot 'SOURCE.json') -Encoding UTF8

    Write-Host 'Publishing Rift Replay (win-x64)...'
    $publishDir = Join-Path $workRoot 'publish'
    $publishArgs = @(
        'publish', 'desktop/LolHighlight.Desktop/LolHighlight.Desktop.csproj',
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publishDir,
        "-p:BundledFfmpegRoot=$ffmpegRoot"
    )
    & dotnet.exe @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish Rift Replay.' }

    foreach ($binary in 'ffmpeg.exe', 'ffprobe.exe') {
        if (!(Test-Path -LiteralPath (Join-Path $publishDir "tools\ffmpeg\bin\$binary"))) {
            throw "The publish directory is missing $binary."
        }
    }

    $wixVersion = '5.0.2'
    $wixDir = Join-Path $cacheRoot "wix-$wixVersion"
    $wix = Join-Path $wixDir 'wix.exe'
    if (!(Test-Path -LiteralPath $wix)) {
        & dotnet.exe tool install wix --tool-path $wixDir --version $wixVersion
        if ($LASTEXITCODE -ne 0) { throw 'Could not download WiX Toolset.' }
    }
    # WiX 5 targets an older .NET runtime. Let the WiX processes use the installed .NET 8 runtime.
    $env:DOTNET_ROLL_FORWARD = 'Major'
    $wixUi = "WixToolset.UI.wixext/$wixVersion"
    & $wix extension add $wixUi
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the WiX installer UI extension.' }

    Write-Host 'Creating the MSI installer...'
    $appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDir 'LolHighlight.Desktop.exe'))
    $msiVersion = "$($appVersion.FileMajorPart).$($appVersion.FileMinorPart).$($appVersion.FileBuildPart)"
    New-Item -ItemType File -Path (Join-Path $workRoot 'installed-mode') | Out-Null
    $setup = Join-Path $workRoot 'RiftReplay-Setup-x64.msi'
    $wixArgs = @(
        'build', 'installer/RiftReplay.wxs', '-arch', 'x64', '-culture', 'zh-CN', '-ext', $wixUi, '-d', "ProductVersion=$msiVersion",
        '-bindpath', "Publish=$publishDir", '-bindpath', "Installer=$workRoot", '-out', $setup
    )
    & $wix @wixArgs
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the MSI installer.' }

    Write-Host 'Creating the portable ZIP...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $portable = Join-Path $workRoot 'RiftReplay-Portable-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $portable)

    Move-Item -LiteralPath $setup, $portable -Destination $outputRoot -Force
    Write-Host "Installer and portable ZIP saved to $outputRoot"
}
finally {
    $env:DOTNET_ROLL_FORWARD = $previousRollForward
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
