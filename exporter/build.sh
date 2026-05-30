#!/bin/bash
# Compile OpenBudgetImporter via SwiftPM, assemble the .app bundle, copy the SQLCipher
# framework into Contents/Frameworks/, fix @rpath, and codesign.
#
# After (re-)building you'll likely need to re-grant Full Disk Access to
# OpenBudgetImporter.app, because the code requirement changes when the binary changes.
#
# Override the signing identity via OPENBUDGET_SIGN_IDENTITY (a SHA-1 fingerprint or the cert's
# common name). Falls back to ad-hoc if the named identity isn't installed.

set -euo pipefail

DIR=$(cd "$(dirname "$0")" && pwd)
APP="$DIR/OpenBudgetImporter.app"
CONTENTS="$APP/Contents"
MACOS_DIR="$CONTENTS/MacOS"
FRAMEWORKS_DIR="$CONTENTS/Frameworks"
BIN="$MACOS_DIR/OpenBudgetImporter"

# Lint first so a style regression fails fast before the (slower) release build. swift-format
# ships with the Swift 6 toolchain as `swift format`; skip gracefully on older toolchains.
if swift format --version >/dev/null 2>&1; then
    echo "Linting Swift sources (swift format lint --strict)..."
    swift format lint --strict --recursive --configuration "$DIR/.swift-format" "$DIR/Sources"
else
    echo "swift format unavailable (needs Swift 6+); skipping lint."
fi

echo "Compiling OpenBudgetImporter (swift build -c release)..."
swift build -c release --package-path "$DIR"

# SwiftPM puts the binary under .build/release/ regardless of arch (on macOS it's also a
# host-triple-specific dir but the symlink `release` resolves correctly for the host arch).
BUILT_BIN="$DIR/.build/release/OpenBudgetImporter"
if [ ! -x "$BUILT_BIN" ]; then
    echo "Build did not produce $BUILT_BIN" >&2
    exit 1
fi

# SQLCipher.framework is extracted from the sqlcipher.swift xcframework. SwiftPM stores it
# under .build/artifacts/<package>/<target>/<framework>.xcframework/macos-arm64_x86_64/...
SQLCIPHER_FRAMEWORK=$(find "$DIR/.build/artifacts" -type d -name 'SQLCipher.framework' \
    -path '*macos*' -not -path '*ios*' -not -path '*tvos*' -not -path '*watchos*' \
    -not -path '*visionos*' -not -path '*catalyst*' | head -n 1)
if [ -z "$SQLCIPHER_FRAMEWORK" ]; then
    echo "Could not locate SQLCipher.framework in .build/artifacts" >&2
    find "$DIR/.build/artifacts" -name 'SQLCipher.framework' 2>/dev/null >&2 || true
    exit 1
fi
echo "Found SQLCipher framework: $SQLCIPHER_FRAMEWORK"

rm -rf "$APP"
mkdir -p "$MACOS_DIR" "$FRAMEWORKS_DIR" "$CONTENTS/Resources"

cat > "$CONTENTS/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>de.beutner.OpenBudgetImporter</string>
    <key>CFBundleName</key>
    <string>OpenBudgetImporter</string>
    <key>CFBundleExecutable</key>
    <string>OpenBudgetImporter</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <!-- Agent app: no dock icon when launchd fires the headless --run-once mode. The
         interactive config flow at the bottom of main.swift calls setActivationPolicy(.regular)
         to elevate to a foreground app and show the window when the user launches it directly. -->
    <key>LSUIElement</key>
    <true/>
</dict>
</plist>
PLIST

cp "$BUILT_BIN" "$BIN"
chmod +x "$BIN"

# Copy the macOS slice of SQLCipher.framework into the bundle. Use `-R` so symlinks
# (Versions/Current → A) are preserved — codesign requires the correct framework layout.
cp -R "$SQLCIPHER_FRAMEWORK" "$FRAMEWORKS_DIR/"

# SwiftPM links against @rpath/SQLCipher.framework/Versions/A/SQLCipher. The executable's
# embedded rpaths point at SwiftPM's build dir; add @executable_path/../Frameworks so the
# runtime loader finds the framework inside the bundle. Also strip the build-dir rpaths so
# the binary isn't sensitive to the developer's home path.
echo "Fixing @rpath..."
# Drop any existing rpaths that reference the SwiftPM build dir.
otool -l "$BIN" | awk '/LC_RPATH/{flag=1;next} flag && /path /{print $2; flag=0}' | while read -r RP; do
    case "$RP" in
        */.build/*|*/checkouts/*|*/artifacts/*)
            install_name_tool -delete_rpath "$RP" "$BIN" 2>/dev/null || true
            ;;
    esac
done
# Add the runtime rpath that points inside the bundle (idempotent — re-add ignores errors).
install_name_tool -add_rpath "@executable_path/../Frameworks" "$BIN" 2>/dev/null || true

# Sign with a real Apple-issued identity by default so the TCC grant survives rebuilds.
# Resolve the identity to a SHA-1 fingerprint so duplicate certs in the keychain (e.g. two
# "Apple Development:..." certs that share a common name) don't make codesign refuse with
# "ambiguous". OPENBUDGET_SIGN_IDENTITY can already be a hash if the caller wants to pin one.
SIGN_IDENTITY="${OPENBUDGET_SIGN_IDENTITY:-Apple Development: Gunnar Beutner (6Y6QK59LBP)}"
SIGN_HASH=$(security find-identity -v -p codesigning \
    | awk -v want="$SIGN_IDENTITY" 'index($0, want) > 0 { print $2; exit }')
if [ -n "$SIGN_HASH" ]; then
    SIGN_ARGS=(--sign "$SIGN_HASH")
    echo "Signing as: $SIGN_IDENTITY ($SIGN_HASH)"
else
    echo "Signing identity \"$SIGN_IDENTITY\" not found; falling back to ad-hoc."
    echo "(TCC will re-prompt for Full Disk Access after every rebuild with ad-hoc signatures.)"
    SIGN_ARGS=(--sign -)
fi

# Sign the framework first (must be a complete, self-consistent signature), then the
# executable, then the bundle. --options=runtime enables Hardened Runtime so the TCC system
# considers the binary suitable for Full Disk Access on modern macOS.
codesign --force "${SIGN_ARGS[@]}" --timestamp=none "$FRAMEWORKS_DIR/SQLCipher.framework"
codesign --force "${SIGN_ARGS[@]}" --options=runtime --timestamp=none "$BIN"
codesign --force "${SIGN_ARGS[@]}" --options=runtime --timestamp=none "$APP"

echo "Verifying signature..."
codesign --verify --deep --strict "$APP"
codesign --display --verbose=2 "$APP" 2>&1 | head -10

echo
echo "Done: $APP"
echo
echo "Next steps:"
echo "  - Grant Full Disk Access to $APP in System Settings"
echo "  - Launch the app once: open \"$APP\""
echo "    (configure base URL + source name + token, then click \"Save & install\")"
