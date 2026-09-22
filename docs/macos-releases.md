# macOS releases

MediaDownloader supports macOS 14 and later, with separate `osx-arm64` (Apple Silicon, including
M1) and `osx-x64` (Intel) archives. The app is self-contained; users do not need to install .NET.

## Why older releases reported “damaged”

v1.0.7 packaged the .NET apphost's ad-hoc signature inside an unsigned bundle. The downloaded
archives matched their checksums, but `codesign --verify --deep --strict` reported
`code has no resources but signature indicates they must be present`. They also lacked Developer
ID signatures and notarization. Re-downloading those same archives does not correct their signing.

The build now embeds managed assemblies into the executable, puts native code in `Contents/MacOS`
and other content in `Contents/Resources`, signs nested code before the complete bundle, and checks
the resulting signature. Local builds and GitHub releases use ad-hoc signatures and do not require
an Apple developer account. These signatures fix bundle integrity; they do not give the app an
Apple-trusted developer identity or notarization ticket.

## Installation on another Mac

1. Download the correct ZIP from this repository's release page and verify it against `SHA256SUMS.txt`.
2. Extract it and move `MediaDownloader.app` to Applications.
3. Try opening it. If macOS blocks it as an unknown developer, go to **System Settings → Privacy &
   Security → Open Anyway**, then confirm **Open**. It runs in the menu bar, without a Dock icon.

This is Apple's per-app approval flow; it does not disable Gatekeeper globally. A new version may
need approval again. If macOS still reports a damaged file, check the archive checksum and run:

```bash
codesign --verify --deep --strict --verbose=2 /Applications/MediaDownloader.app
```

Do not approve a copy with a mismatched checksum or a broken signature. Ad-hoc signing does not
guarantee identical Gatekeeper wording across macOS versions; the first-launch approval is still
necessary for downloaded software without notarization.

## GitHub configuration

No Apple certificates, Apple account or signing secrets are required. The release workflow ad-hoc
signs both architectures, verifies the bundles, tests the extracted archives on native hardware,
and publishes checksums plus a first-launch notice. To release, bump the project version, commit,
and push a matching `vX.Y.Z` tag using the normal release process. Existing v1.0.7 assets are not
changed by local builds.

## Local builds and verification

```bash
./build-macos-app.sh --rid osx-arm64
python3 scripts/test-macos-package.py dist/MediaDownloader-*-osx-arm64.zip
```

Run the smoke test with exactly one archive matching the host architecture. It extracts the ZIP,
checks the signature and minimum OS version, starts the app with a temporary data directory,
checks the dashboard, database health, JavaScript and CSS, then tests native tray startup and
graceful shutdown. It also tests update preparation with the real signing tools and rejects a
tampered replacement. It checks the bundle signature again afterward. It does not use the installed
user's database or enable notifications/crash reporting.

Ad-hoc builds verify bundle integrity but do not establish a trusted developer identity or
Gatekeeper acceptance. They do not enable hardened runtime because ad-hoc signatures have no Team
ID for native library validation.

## Optional notarization in the future

If you later obtain an Apple Developer Program membership, install a Developer ID Application
identity in Keychain and save a notarization profile using `xcrun notarytool store-credentials`.
This optional command uses hardened runtime, signs SQLite and the app with the same identity,
notarizes and staples; the current GitHub workflow deliberately uses the ad-hoc default:

```bash
export MACOS_SIGNING_IDENTITY='Developer ID Application: Your Name (TEAMID)'
export MACOS_NOTARY_PROFILE='your-notary-profile'
./build-macos-app.sh --notarize --rid osx-arm64
```

If the notarization profile is stored in a non-default keychain, also set `MACOS_NOTARY_KEYCHAIN`
to its path. The script archives once for notarization, staples the resulting app, and creates the
final ZIP afterward so the ticket survives extraction and is available offline.

## Updating and recovery

The updater refuses downloads with a missing or mismatched checksum and verifies the replacement's
bundle signature and application identifier before stopping the running app. Ad-hoc installations
accept correctly ad-hoc-signed updates without requiring Apple notarization. Certificate-signed
installations retain the additional Gatekeeper check. This policy is determined from the installed
app, not from the downloaded replacement. Checksums and ad-hoc signatures detect corruption; they
do not authenticate the publisher, so downloads come from the configured GitHub repository over HTTPS.
It first copies to a private directory beside the installed bundle, so disk-space or permission
failures do not delete the working app. The installation helper waits for shutdown, renames the
old app into `Previous.app`, promotes the new app, and invokes Launch Services. If a rename or
launch command fails, it restores the previous app.

The helper retains `.MediaDownloader-update-<id>/Previous.app` and `install.log` beside the installed
application. If the new app crashes after Launch Services accepts the launch, quit it and restore
that backup manually. Automatic rollback covers failed installation commands, not later crashes.
The backup is the app only; it does not roll back database migrations. Remove old backup directories
after confirming an update works. The updater never strips quarantine to bypass Gatekeeper.

## References

- [Apple: placing content in a bundle](https://developer.apple.com/documentation/bundleresources/placing-content-in-a-bundle)
- [Apple: opening an app from an unknown developer](https://support.apple.com/guide/mac-help/mh40616/mac)
- [Microsoft: publishing .NET apps for macOS](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos)
- [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
