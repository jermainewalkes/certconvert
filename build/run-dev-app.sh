#!/usr/bin/env bash
# Builds the Debug binary and launches it as a proper macOS .app (with the real
# icon) so the Dock looks right during development. The bundle wraps the Debug
# output in place — no publish step, so rebuild-and-relaunch stays fast.
set -euo pipefail

cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

dotnet build src/CertConvert

APP="bin-dev/CertConvert.app"
BIN="$PWD/src/CertConvert/bin/Debug/net10.0/CertConvert"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>CertConvert</string>
    <key>CFBundleDisplayName</key><string>CertConvert</string>
    <key>CFBundleIdentifier</key><string>com.certconvert.app.dev</string>
    <key>CFBundleExecutable</key><string>CertConvert</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleIconFile</key><string>CertConvert</string>
    <key>CFBundleShortVersionString</key><string>0.0.0-dev</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

cat > "$APP/Contents/MacOS/CertConvert" <<LAUNCH
#!/bin/zsh
export DOTNET_ROOT="\${DOTNET_ROOT:-\$HOME/.dotnet}"
exec "$BIN" "\$@"
LAUNCH
chmod +x "$APP/Contents/MacOS/CertConvert"
cp src/CertConvert/Assets/CertConvert.icns "$APP/Contents/Resources/"

pkill -f "net10.0/CertConvert" 2>/dev/null || true
open "$APP"
echo "Launched $APP"
