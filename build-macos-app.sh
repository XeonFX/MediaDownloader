#!/usr/bin/env bash
# Builds MediaDownloader.app - a self-contained macOS menu-bar app (no Dock icon).
# Usage: ./build-macos-app.sh [--version X.Y.Z] [--rid osx-arm64|osx-x64]
#   (output: ./dist/MediaDownloader.app; defaults: version from csproj, RID from host arch)
set -eo pipefail
cd "$(dirname "$0")"

VERSION=""
RID=""
while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --rid)     RID="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

if [ -z "$RID" ]; then
  ARCH="$(uname -m)"
  if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
  elif [ "$ARCH" = "x86_64" ]; then
    RID="osx-x64"
  else
    echo "Unsupported architecture: $ARCH" >&2
    exit 1
  fi
fi

# Default version: the <Version> in the csproj (kept in sync with release tags).
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' MediaDownloader.csproj)"
  VERSION="${VERSION:-1.0.0}"
fi

APP="dist/MediaDownloader.app"
echo "Publishing $VERSION for $RID..."
rm -rf "$APP" dist/publish
# Publish the project (not the .slnx solution) so -o is honoured and outputs land in dist/publish.
dotnet publish MediaDownloader.csproj -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:Version="$VERSION" -o dist/publish

echo "Assembling $APP..."
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R dist/publish/. "$APP/Contents/MacOS/"
cp Assets/AppIcon.icns "$APP/Contents/Resources/"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>MediaDownloader</string>
    <key>CFBundleDisplayName</key>     <string>MediaDownloader</string>
    <key>CFBundleIdentifier</key>      <string>com.mediadownloader.app</string>
    <key>CFBundleVersion</key>         <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key> <string>${VERSION}</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleExecutable</key>      <string>MediaDownloader</string>
    <key>CFBundleIconFile</key>        <string>AppIcon</string>
    <!-- Menu-bar agent: no Dock icon, no app-switcher entry. -->
    <key>LSUIElement</key>             <true/>
    <key>LSMinimumSystemVersion</key>  <string>11.0</string>
    <key>NSHighResolutionCapable</key> <true/>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/MediaDownloader"
rm -rf dist/publish
echo "Done -> $APP"
echo "Run it with:  open \"$APP\"   (look for the down-arrow icon in the menu bar)"
