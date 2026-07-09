# Builds a self-contained Windows release of MediaDownloader.
# Usage: ./build-windows-app.ps1 [-Version X.Y.Z] [-Rid win-x64|win-arm64]
#   (output: ./dist/MediaDownloader-<version>-<rid>.zip)
# On Windows, running MediaDownloader.exe adds a system-tray icon (WindowsTrayApp) with a
# Dashboard/Check-for-updates/Quit context menu, mirroring the macOS menu-bar agent. Set
# MD_NO_TRAY=1 to run headless instead. The dashboard is at the URL it prints (default
# http://localhost:47820).
param(
    [string]$Version = "",
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Default version: the <Version> in the csproj (kept in sync with release tags).
if (-not $Version) {
    $match = Select-String -Path "MediaDownloader.csproj" -Pattern "<Version>(.*)</Version>"
    $Version = if ($match) { $match.Matches[0].Groups[1].Value } else { "1.0.0" }
}

$publishDir = "dist/publish-win"
$zipPath = "dist/MediaDownloader-$Version-$Rid.zip"

Write-Host "Publishing $Version for $Rid..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Publish the project (not the .slnx solution) so -o is honoured.
dotnet publish MediaDownloader.csproj -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=false -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Zipping $zipPath..."
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath
Remove-Item $publishDir -Recurse -Force

Write-Host "Done -> $zipPath"
Write-Host "Unzip it anywhere and run MediaDownloader.exe -- it adds a system-tray icon; open the printed dashboard URL from there or a browser."
