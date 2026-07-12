#!/usr/bin/env bash
# Publishes self-contained, single-file CertConvert builds for every target.
# Requires the .NET 10 SDK on PATH (or DOTNET_ROOT set for a user-local install).
#
# Usage: build/publish.sh [rid ...]
#   With no arguments, builds osx-x64, osx-arm64 and win-x64.
set -euo pipefail

cd "$(dirname "$0")/.."
PROJECT="src/CertConvert/CertConvert.csproj"
OUT="artifacts"
VERSION="$(grep -oE '<Version>[^<]+' "$PROJECT" | head -1 | sed 's/<Version>//')"

RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then
    RIDS=(osx-x64 osx-arm64 win-x64)
fi

rm -rf "$OUT"
for rid in "${RIDS[@]}"; do
    echo "==> Publishing $rid"
    dest="$OUT/$rid"
    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:DebugType=none \
        -o "$dest"

    case "$rid" in
        osx-*)  build/make-macos-app.sh "$rid" "$dest" "$VERSION" ;;
        win-*)  ( cd "$OUT" && zip -qr "CertConvert-$VERSION-$rid.zip" "$rid" ) ;;
    esac
    echo "    done: $dest"
done

# Checksums file — uploaded with releases so the in-app updater can verify downloads.
( cd "$OUT" && shasum -a 256 *.zip > SHA256SUMS.txt )

echo "All artifacts in $OUT/ (version $VERSION)."
