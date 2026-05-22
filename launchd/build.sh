#!/bin/bash
# Compile the Swift launcher, drop it into OpenBudgetImporter.app, and ad-hoc sign it.
# Run from anywhere; paths resolve relative to this script.
#
# After (re-)building you'll likely need to re-grant Full Disk Access to
# OpenBudgetImporter.app, because the code requirement changes when the binary changes.

set -e

DIR=$(cd "$(dirname "$0")" && pwd)
APP="$DIR/OpenBudgetImporter.app"
SRC="$DIR/OpenBudgetImporter.swift"
BIN="$APP/Contents/MacOS/OpenBudgetImporter"

mkdir -p "$APP/Contents/MacOS"

# Bundle metadata — recreated on every build so it stays in sync with this script.
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.openbudget.export</string>
    <key>CFBundleName</key>
    <string>OpenBudgetImporter</string>
    <key>CFBundleExecutable</key>
    <string>OpenBudgetImporter</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>LSUIElement</key>
    <true/>
</dict>
</plist>
PLIST

echo "Compiling launcher..."
swiftc -O -o "$BIN" "$SRC"
chmod +x "$BIN"

# Sign with a real Apple-issued identity by default so the TCC grant survives rebuilds.
# Override via the OPENBUDGET_SIGN_IDENTITY env var (a SHA-1 fingerprint or the cert's common name).
# Falls back to ad-hoc if the named identity isn't installed.
SIGN_IDENTITY="${OPENBUDGET_SIGN_IDENTITY:-Apple Development: Gunnar Beutner (6Y6QK59LBP)}"
if security find-identity -v -p codesigning | grep -q "$SIGN_IDENTITY"; then
    echo "Signing bundle as: $SIGN_IDENTITY"
    codesign --force --deep --sign "$SIGN_IDENTITY" "$APP"
else
    echo "Signing identity \"$SIGN_IDENTITY\" not found; falling back to ad-hoc."
    echo "(TCC will re-prompt for Full Disk Access after every rebuild with ad-hoc signatures.)"
    codesign --force --deep --sign - "$APP"
fi

echo "Done: $APP"
echo
echo "Next steps:"
echo "  - Grant Full Disk Access to $APP in System Settings"
echo "  - cp $DIR/com.openbudget.export.plist ~/Library/LaunchAgents/ (edit path if needed)"
echo "  - launchctl bootstrap gui/\$(id -u) ~/Library/LaunchAgents/com.openbudget.export.plist"
