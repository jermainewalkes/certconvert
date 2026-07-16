#!/usr/bin/env bash
# Publishes self-contained, single-file CertConvert builds for every target.
# Requires the .NET 10 SDK on PATH (or DOTNET_ROOT set for a user-local install).
#
# Usage: build/publish.sh [--docker-win] [rid ...]
#   With no RID arguments, builds osx-x64, osx-arm64 and win-x64.
#   --docker-win  delegates the win-x64 publish to ./build-in-docker.sh win-x64
#                 (Linux SDK container) instead of the host dotnet — handy when
#                 no host SDK is on PATH. Position-independent; everything else
#                 (osx publishes, bundling, zipping, checksums) is unchanged.
set -euo pipefail

cd "$(dirname "$0")/.."
PROJECT="src/CertConvert/CertConvert.csproj"
OUT="artifacts"
VERSION="$(grep -oE '<Version>[^<]+' "$PROJECT" | head -1 | sed 's/<Version>//')"

# Pull the position-independent --docker-win flag out; leave RIDs in order.
DOCKER_WIN=0
RIDS=()
for arg in "$@"; do
    case "$arg" in
        --docker-win) DOCKER_WIN=1 ;;
        *)            RIDS+=("$arg") ;;
    esac
done
if [ ${#RIDS[@]} -eq 0 ]; then
    RIDS=(osx-x64 osx-arm64 win-x64)
fi

# Clean only this script's own outputs. artifacts/ is shared: the store
# packagers write artifacts/mas and artifacts/msix (signed, expensive to
# rebuild) and the docker gate caches under artifacts/docker-build.
mkdir -p "$OUT"
rm -rf "$OUT"/osx-x64 "$OUT"/osx-arm64 "$OUT"/win-x64 \
       "$OUT"/CertConvert-*.zip "$OUT"/SHA256SUMS.txt
for rid in "${RIDS[@]}"; do
    echo "==> Publishing $rid"
    dest="$OUT/$rid"
    if [ "$rid" = "win-x64" ] && [ "$DOCKER_WIN" -eq 1 ]; then
        # Container produces artifacts/win-x64 with identical publish properties;
        # zipping and checksums below stay on the host, unchanged.
        ./build-in-docker.sh win-x64
    else
        dotnet publish "$PROJECT" \
            -c Release \
            -r "$rid" \
            --self-contained true \
            -p:PublishSingleFile=true \
            -p:DebugType=none \
            -o "$dest"
    fi

    case "$rid" in
        osx-*)  build/make-macos-app.sh "$rid" "$dest" "$VERSION" ;;
        win-*)  ( cd "$OUT" && zip -qr "CertConvert-$VERSION-$rid.zip" "$rid" ) ;;
    esac
    echo "    done: $dest"
done

# Checksums file — uploaded with releases so the in-app updater can verify downloads.
( cd "$OUT" && shasum -a 256 *.zip > SHA256SUMS.txt )

echo "All artifacts in $OUT/ (version $VERSION)."
