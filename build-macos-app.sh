#!/usr/bin/env bash
# Builds MediaDownloader.app - a self-contained macOS menu-bar app (no Dock icon).
# Usage: ./build-macos-app.sh [--version X.Y.Z] [--rid osx-arm64|osx-x64] [--notarize]
# Local and release builds are ad-hoc signed by default. Optional --notarize requires MACOS_SIGNING_IDENTITY and
# MACOS_NOTARY_PROFILE (a notarytool keychain profile) and produces a notarized archive.
#   (output: ./dist/MediaDownloader.app; defaults: version from csproj, RID from host arch)
set -euo pipefail
cd "$(dirname "$0")"

VERSION=""
RID=""
NOTARIZE=false
while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="${2:?--version requires X.Y.Z}"; shift 2 ;;
    --rid)     RID="${2:?--rid requires osx-arm64 or osx-x64}"; shift 2 ;;
    --notarize) NOTARIZE=true; shift ;;
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
case "$RID" in osx-arm64|osx-x64) ;; *) echo "Unsupported RID: $RID" >&2; exit 1 ;; esac
if $NOTARIZE; then
  : "${MACOS_SIGNING_IDENTITY:?Notarization requires a Developer ID Application identity}"
  : "${MACOS_NOTARY_PROFILE:?Notarization requires a notarytool keychain profile}"
  case "$MACOS_SIGNING_IDENTITY" in
    "Developer ID Application: "*) ;;
    *) echo "Use a Developer ID Application signing identity for notarization" >&2; exit 1 ;;
  esac
fi

# Default version: the <Version> in the csproj (kept in sync with release tags).
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' MediaDownloader.csproj)"
  VERSION="${VERSION:-1.0.0}"
fi
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must be X.Y.Z" >&2; exit 1
fi

APP="dist/MediaDownloader.app"
echo "Publishing $VERSION for $RID..."
rm -rf "$APP" dist/publish
# Publish the project (not the .slnx solution) so -o is honoured and outputs land in dist/publish.
dotnet publish MediaDownloader.csproj -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:DebugType=None -p:DebugSymbols=false -p:Version="$VERSION" -o dist/publish

echo "Assembling $APP..."
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R dist/publish/. "$APP/Contents/Resources/"
# Embed managed assemblies in the apphost. Only Mach-O code belongs in Contents/MacOS;
# signing the old layout tried to treat managed .dll files as nested native executables.
for item in "$APP/Contents/Resources/"*; do
  if [ -f "$item" ] && /usr/bin/file -b "$item" | /usr/bin/grep -q 'Mach-O'; then
    mv "$item" "$APP/Contents/MacOS/"
  fi
done
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
    <key>LSMinimumSystemVersion</key>  <string>14.0</string>
    <key>NSHighResolutionCapable</key> <true/>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/MediaDownloader"
SIGNING_IDENTITY="-"
# Ad-hoc identities have no Team ID, so hardened library validation cannot establish
# that the SQLite library belongs to this app. Notarized builds sign both with the same Developer ID.
SIGNING_OPTIONS=(--timestamp=none)
if $NOTARIZE; then
  SIGNING_IDENTITY="$MACOS_SIGNING_IDENTITY"
  SIGNING_OPTIONS=(--timestamp --options runtime)
fi
# Sign nested native libraries/tools first, then seal the complete app. Do not use --deep
# for signing: it can silently miss nested code. --deep is used only for verification.
while IFS= read -r -d '' native; do
  [ "$native" = "$APP/Contents/MacOS/MediaDownloader" ] && continue
  /usr/bin/codesign --force --sign "$SIGNING_IDENTITY" "${SIGNING_OPTIONS[@]}" "$native"
done < <(find "$APP/Contents/MacOS" -type f -print0)
/usr/bin/codesign --force --sign "$SIGNING_IDENTITY" "${SIGNING_OPTIONS[@]}" \
  --entitlements Assets/macos-entitlements.plist "$APP"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$APP"

ZIP="dist/MediaDownloader-$VERSION-$RID.zip"
if $NOTARIZE; then
  /usr/bin/ditto -c -k --keepParent "$APP" "$ZIP"
  NOTARY_OPTIONS=(--keychain-profile "$MACOS_NOTARY_PROFILE")
  if [ -n "${MACOS_NOTARY_KEYCHAIN:-}" ]; then
    NOTARY_OPTIONS+=(--keychain "$MACOS_NOTARY_KEYCHAIN")
  fi
  xcrun notarytool submit "$ZIP" "${NOTARY_OPTIONS[@]}" --wait
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  /usr/sbin/spctl --assess --type execute --verbose=2 "$APP"
fi
# Recreate after stapling so offline clients receive the ticket too.
rm -f "$ZIP"
/usr/bin/ditto -c -k --keepParent "$APP" "$ZIP"
rm -rf dist/publish
echo "Done -> $APP"
if ! $NOTARIZE; then
  echo "Ad-hoc signed build: integrity verified; macOS may require Open Anyway on first launch."
fi
echo "Run it with:  open \"$APP\"   (look for the down-arrow icon in the menu bar)"
