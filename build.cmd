@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "ffmpegSource=%~1"
if not "%~2"=="" goto :usage
if not defined ffmpegSource goto :start
if /i "%ffmpegSource%"=="global" goto :start
if /i "%ffmpegSource%"=="cn" goto :start

:usage
echo Usage: build.cmd [global^|cn] >&2
exit /b 2

:start
pushd "%~dp0" || exit /b 1
call :main
set "result=%errorlevel%"
popd
exit /b %result%

:main
where.exe dotnet.exe >nul 2>&1 || (
    echo Install the .NET 8 SDK ^(x64^) before running this script. >&2
    exit /b 1
)
set "sdkVersion="
for /f "delims=" %%V in ('dotnet.exe --version') do set "sdkVersion=%%V"
if not "%sdkVersion:~0,2%"=="8." (
    echo The .NET 8 SDK ^(x64^) is required. See global.json. >&2
    exit /b 1
)

set "outputRoot=%CD%\build"
set "cacheRoot=%CD%\.runtime"
set "workRoot=%outputRoot%\work-%RANDOM%-%RANDOM%-%RANDOM%"
mkdir "%workRoot%" || exit /b 1

call :build
set "result=%errorlevel%"
rmdir /s /q "%workRoot%" || exit /b 1
exit /b %result%

:build
if not exist "%cacheRoot%\" mkdir "%cacheRoot%"
if not exist "%cacheRoot%\" exit /b 1

set "ffmpegVersion=2026-10-29-git-31ba826726"
set "ffmpegPackage=ffmpeg-%ffmpegVersion%-essentials_build"
set "ffmpegUrl=https://github.com/GyanD/codexffmpeg/releases/download/%ffmpegVersion%/%ffmpegPackage%.7z"
set "ffmpegAsset=https://api.github.com/repos/GyanD/codexffmpeg/releases/assets/688150779"
rem Size of the pinned archive, also used when the download has no Content-Length.
set "ffmpegSize=36325390"
set "ffmpegHash=94c9d4eae0817f4c2cc8c78fe2d001db347ce4a433a52f1d9312ff6a886451f8"
set "archive=%cacheRoot%\%ffmpegPackage%.7z"
set "hashFile=%archive%"
call :verify_ffmpeg || call :download_ffmpeg || exit /b 1

tar.exe -xf "%archive%" -C "%workRoot%" || (
    echo Could not extract FFmpeg. >&2
    exit /b 1
)
set "ffmpegRoot=%workRoot%\%ffmpegPackage%"
(
    echo {
    echo   "url": "%ffmpegUrl%",
    echo   "sha256": "%ffmpegHash%",
    echo   "source": "https://github.com/FFmpeg/FFmpeg/tree/31ba826726"
    echo }
) >"%ffmpegRoot%\SOURCE.json" || exit /b 1

echo Publishing Rift Replay ^(win-x64^)...
set "project=desktop\LolHighlight.Desktop\LolHighlight.Desktop.csproj"
set "publishDir=%workRoot%\publish"
dotnet.exe publish "%project%" -c Release -r win-x64 --self-contained true -o "%publishDir%" "-p:BundledFfmpegRoot=%ffmpegRoot%" || (
    echo Could not publish Rift Replay. >&2
    exit /b 1
)

for %%F in (ffmpeg.exe ffprobe.exe) do (
    if not exist "%publishDir%\tools\ffmpeg\bin\%%F" (
        echo The publish directory is missing %%F. >&2
        exit /b 1
    )
)

rem Read the SDK-generated file version and use its first three components for MSI.
dotnet.exe msbuild "%project%" -nologo -t:GetAssemblyVersion -getProperty:FileVersion -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true "-p:BundledFfmpegRoot=%ffmpegRoot%" >"%workRoot%\version.txt" || (
    type "%workRoot%\version.txt" >&2
    echo Could not determine the MSI version. >&2
    exit /b 1
)
set "msiVersion="
for /f "usebackq tokens=1-3 delims=." %%A in ("%workRoot%\version.txt") do set "msiVersion=%%A.%%B.%%C"
if not defined msiVersion exit /b 1

set "wixVersion=5.0.2"
set "wixDir=%cacheRoot%\wix-%wixVersion%"
set "wix=%wixDir%\wix.exe"
if not exist "%wix%" (
    dotnet.exe tool install wix --tool-path "%wixDir%" --version %wixVersion% || (
        echo Could not download WiX Toolset. >&2
        exit /b 1
    )
)
rem WiX 5 can roll forward to the installed .NET 8 runtime.
set "DOTNET_ROLL_FORWARD=Major"
set "wixUi=WixToolset.UI.wixext/%wixVersion%"
"%wix%" extension add "%wixUi%" || (
    echo Could not restore the WiX installer UI extension. >&2
    exit /b 1
)

echo Creating the MSI installer...
type nul >"%workRoot%\installed-mode" || exit /b 1
set "setup=%workRoot%\RiftReplay-Setup-x64.msi"
"%wix%" build installer\RiftReplay.wxs -arch x64 -culture zh-CN -ext "%wixUi%" -d "ProductVersion=%msiVersion%" ^
    -bindpath "Publish=%publishDir%" -bindpath "Installer=%workRoot%" -out "%setup%" || (
    echo Could not create the MSI installer. >&2
    exit /b 1
)

echo Creating the portable ZIP...
set "portable=%workRoot%\RiftReplay-Portable-x64.zip"
tar.exe -a -cf "%portable%" -C "%publishDir%" . || (
    echo Could not create the portable ZIP. >&2
    exit /b 1
)

move /y "%setup%" "%outputRoot%\" >nul || exit /b 1
move /y "%portable%" "%outputRoot%\" >nul || exit /b 1
echo Installer and portable ZIP saved to "%outputRoot%"
exit /b 0

:verify_ffmpeg
if not exist "%hashFile%" exit /b 1
certutil.exe -hashfile "%hashFile%" SHA256 >"%workRoot%\ffmpeg-sha256.txt" 2>nul || exit /b 1
findstr.exe /i /l /x /c:"%ffmpegHash%" "%workRoot%\ffmpeg-sha256.txt" >nul
exit /b %errorlevel%

:download_ffmpeg
set "hashFile=%workRoot%\%ffmpegPackage%.7z"
dotnet.exe build "tools\FfmpegDownloader\FfmpegDownloader.csproj" --artifacts-path "%workRoot%\downloader" ^
    -o "%workRoot%\downloader\app" --nologo >"%workRoot%\downloader-build.log" || (
    type "%workRoot%\downloader-build.log" >&2
    exit /b 1
)
dotnet.exe "%workRoot%\downloader\app\FfmpegDownloader.dll" ^
    "%ffmpegSize%" "%hashFile%" "%ffmpegAsset%" "%ffmpegUrl%" "%ffmpegSource%" || (
    echo Could not download FFmpeg. Rerun build.cmd to select a download source. >&2
    exit /b 1
)
call :verify_ffmpeg || (
    echo The FFmpeg archive did not match the expected SHA-256. >&2
    exit /b 1
)
move /y "%hashFile%" "%archive%" >nul
exit /b %errorlevel%
