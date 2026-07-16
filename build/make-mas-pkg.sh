#!/usr/bin/env bash
# make-mas-pkg.sh — build a Mac App Store .pkg for CertConvert.
#
#   build/make-mas-pkg.sh
#
# Produces artifacts/mas/CertConvert.pkg, signed and ready to upload to App
# Store Connect (Transporter or `xcrun altool`/`notarytool`-adjacent tools).
#
# WHY THIS IS FIDDLY (learned the hard way — see build/RELEASING.md):
#   * Avalonia under App Sandbox aborts at launch (issue #6529: no LaunchServices
#     ASN) UNLESS the app is published single-file, so LaunchServices registers a
#     single mach-o. Multi-file layouts (managed DLLs loose in MacOS/) crash.
#   * But plain single-file SELF-EXTRACTS its native dylibs to a cache at runtime,
#     and those unsigned extracted copies make the sandbox flag the app "damaged".
#     So we publish single-file WITH IncludeNativeLibrariesForSelfExtract=false —
#     the managed code stays bundled in the apphost, the 3 native dylibs sit as
#     real files we sign individually. No runtime extraction, nothing unsigned.
#   * hardened runtime is NOT used (MAS doesn't require it; the sandbox is the
#     security boundary). spctl will "reject" the result — that's expected for an
#     App-Store-signed app (trusted via receipt, not Gatekeeper).
set -euo pipefail

cd "$(dirname "$0")/.."
PROJECT="src/CertConvert/CertConvert.csproj"
VERSION="$(grep -oE '<Version>[^<]+' "$PROJECT" | head -1 | sed 's/<Version>//')"

# --- configuration (override via env) ---------------------------------------
RID="${MAS_RID:-osx-arm64}"                       # arm64 only for now (see universal note below)
BUNDLE_ID="${MAS_BUNDLE_ID:-com.certconvert.app}"
TEAM_ID="${MAS_TEAM_ID:-WAG8MH2U2W}"
APP_CERT="${MAS_APP_CERT:-Apple Distribution: JERMAINE WALKES (WAG8MH2U2W)}"
INSTALLER_CERT="${MAS_INSTALLER_CERT:-3rd Party Mac Developer Installer: JERMAINE WALKES (WAG8MH2U2W)}"
PROFILE="${MAS_PROFILE:-design/CertConvert_Mac_App_Store.provisionprofile}"

OUT="artifacts/mas"
APP="$OUT/CertConvert.app"
PKG="$OUT/CertConvert-$VERSION.pkg"

# --- preflight --------------------------------------------------------------
[ -f "$PROFILE" ] || { echo "ERROR: provisioning profile not found at $PROFILE" >&2
  echo "  Generate a 'Mac App Store' distribution profile for $BUNDLE_ID in the" >&2
  echo "  Apple Developer portal and save it there." >&2; exit 1; }
security find-identity -v 2>/dev/null | grep -qF "$APP_CERT" || {
  echo "ERROR: signing identity not found: $APP_CERT" >&2; exit 1; }
security find-identity -v 2>/dev/null | grep -qF "$INSTALLER_CERT" || {
  echo "ERROR: installer identity not found: $INSTALLER_CERT" >&2; exit 1; }

rm -rf "$OUT"; mkdir -p "$OUT"

# --- publish: single-file managed, native libs NOT self-extracted -----------
echo "==> Publishing $RID (store variant, no native self-extract)"
PUB="$OUT/publish"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:DebugType=none \
  -p:StoreBuild=true \
  --artifacts-path "$OUT/int" -o "$PUB"

# --- assemble the bundle ----------------------------------------------------
echo "==> Assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUB/CertConvert" "$APP/Contents/MacOS/"
cp "$PUB"/*.dylib "$APP/Contents/MacOS/"
cp src/CertConvert/Assets/CertConvert.icns "$APP/Contents/Resources/"
cp "$PROFILE" "$APP/Contents/embedded.provisionprofile"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>CertConvert</string>
  <key>CFBundleDisplayName</key><string>CertConvert</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>CertConvert</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>CertConvert</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict></plist>
PLIST

# --- entitlements (must be a subset of what the profile authorises) ---------
ENT="$OUT/entitlements.plist"
cat > "$ENT" <<ENTS
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.application-identifier</key><string>$TEAM_ID.$BUNDLE_ID</string>
  <key>com.apple.developer.team-identifier</key><string>$TEAM_ID</string>
  <key>com.apple.security.app-sandbox</key><true/>
  <key>com.apple.security.files.user-selected.read-write</key><true/>
</dict></plist>
ENTS

# --- sign: each dylib, then the apphost (entitled), then the bundle ---------
echo "==> Signing"
for d in "$APP/Contents/MacOS"/*.dylib; do
  codesign --force --timestamp --sign "$APP_CERT" "$d"
done
codesign --force --timestamp --entitlements "$ENT" --sign "$APP_CERT" "$APP/Contents/MacOS/CertConvert"
codesign --force --timestamp --entitlements "$ENT" --sign "$APP_CERT" "$APP"
codesign --verify --strict --deep "$APP"
echo "    signed; verifying entitlements:"
codesign -d --entitlements - "$APP" 2>/dev/null | grep -c "app-sandbox" >/dev/null && echo "    sandbox entitlement present"

# --- build the installer package --------------------------------------------
echo "==> Building $PKG"
productbuild --component "$APP" /Applications --sign "$INSTALLER_CERT" "$PKG"
echo "    verifying package signature:"
pkgutil --check-signature "$PKG" | head -4

echo
echo "Done: $PKG (version $VERSION, $RID)"
echo "Upload to App Store Connect with Transporter, or:"
echo "  xcrun altool --upload-app -f \"$PKG\" -t macos --apiKey <KEYID> --apiIssuer <ISSUER>"
echo
echo "NOTE: this is an arm64-only package. Intel Macs will not be offered it."
echo "Universal builds need lipo across two single-file apphosts (the appended"
echo "single-file bundle data makes that non-trivial) — deferred; revisit if"
echo "Intel coverage is required."
