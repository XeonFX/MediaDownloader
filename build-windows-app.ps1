# Builds a self-contained Windows release of MediaDownloader.
# Usage: ./build-windows-app.ps1 [-Version X.Y.Z] [-Rid win-x64|win-arm64]
#   (output: ./dist/MediaDownloader-<version>-<rid>.zip)
# On Windows the app runs as a plain web server (no tray): run MediaDownloader.exe
# and open the dashboard at the URL it prints (default http://localhost:47820).
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
Write-Host "Unzip it anywhere and run MediaDownloader.exe, then open the printed dashboard URL."
