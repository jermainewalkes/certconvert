#!/usr/bin/env bash
# Wraps a published macOS build into a double-clickable CertConvert.app and zips it.
# Called by publish.sh; can also be run standalone: make-macos-app.sh <rid> <publish-dir> <version>
set -euo pipefail

rid="$1"
publish_dir="$2"
version="$3"
cd "$(dirname "$0")/.."

app="artifacts/CertConvert.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# Move the whole self-contained payload into the bundle; the executable is CertConvert.
cp -R "$publish_dir/." "$app/Contents/MacOS/"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>CertConvert</string>
    <key>CFBundleDisplayName</key><string>CertConvert</string>
    <key>CFBundleIdentifier</key><string>uk.jermainewalkes.certconvert</string>
    <key>CFBundleExecutable</key><string>CertConvert</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

chmod +x "$app/Contents/MacOS/CertConvert"
# Ad-hoc sign so Gatekeeper on the build machine treats it as a valid (if unsigned) bundle.
codesign --force --deep --sign - "$app" 2>/dev/null || \
    echo "    (codesign unavailable — bundle is unsigned)"

( cd artifacts && zip -qr "CertConvert-$version-$rid.zip" "CertConvert.app" )
rm -rf "$app"
