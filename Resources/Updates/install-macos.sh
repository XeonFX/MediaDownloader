#!/bin/bash
# Embedded in the application; arguments are passed verbatim, never interpolated into shell code.
set -euo pipefail
mode="$1"; bundle="$2"; source_app="$3"; work="$4"
replacement="$work/Replacement.app"
backup="$work/Previous.app"
exec >> "$work/install.log" 2>&1

if [ "$mode" = prepare ]; then
    /usr/bin/ditto "$source_app" "$replacement"
    /usr/bin/codesign --verify --deep --strict "$replacement"
    /usr/bin/codesign --verify -R '=identifier "com.mediadownloader.app"' "$replacement"
    # Ad-hoc releases intentionally have no Apple trust ticket. Preserve integrity and bundle
    # identity checks above; requiring notarization here would make every ad-hoc update fail.
    # Keep Gatekeeper enforcement for an installation signed with a certificate.
    installed_signature="$(/usr/bin/codesign --display --verbose=2 "$bundle" 2>&1)"
    if printf '%s\n' "$installed_signature" | /usr/bin/grep -qx 'Signature=adhoc'; then
        echo "Ad-hoc installation: replacement signature and identifier verified."
    else
        /usr/sbin/spctl --assess --type execute "$replacement"
    fi
    exit 0
fi

[ "$mode" = install ] || exit 1
pid="$5"
for ((i=0; i<120; i++)); do
    kill -0 "$pid" 2>/dev/null || break
    /bin/sleep 0.5
done
if kill -0 "$pid" 2>/dev/null; then
    echo "Application did not exit; leaving the installed app untouched."
    exit 1
fi

# All paths share the destination filesystem, so each rename is atomic. Keep the backup and
# log after success for recovery if the new app later fails during startup.
moved_old=0
installed_new=0
rollback() {
    result=$?
    trap - EXIT
    if [ "$result" -ne 0 ] && [ "$moved_old" -eq 1 ]; then
        if [ "$installed_new" -eq 1 ]; then
            /bin/mv "$bundle" "$work/Failed.app" || exit "$result"
        fi
        /bin/mv "$backup" "$bundle" || exit "$result"
        /usr/bin/open "$bundle" || true
        echo "Update failed; restored the previous application."
    fi
    exit "$result"
}
trap rollback EXIT
if [ ! -d "$replacement" ] || [ -e "$backup" ]; then
    echo "Replacement missing or backup already exists; refusing installation."
    exit 1
fi
/bin/mv "$bundle" "$backup"
moved_old=1
/bin/mv "$replacement" "$bundle"
installed_new=1
/usr/bin/open "$bundle"
echo "Update installed. Previous application retained at $backup"
