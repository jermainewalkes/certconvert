#!/usr/bin/env bash
# build-in-docker.sh — build, test and publish CertConvert with no Windows VM
# and no host .NET SDK. Everything runs in the Linux .NET 10 SDK container.
#
#   ./build-in-docker.sh            verification gate: both test suites + a Release build
#   ./build-in-docker.sh win-x64    self-contained single-file win-x64 publish -> artifacts/win-x64
#
# CertConvert targets plain net10.0 (no Windows APIs), so it all runs in a Linux
# .NET 10 SDK container and the win-x64 artifact cross-compiles cleanly. The
# win-x64 mode only produces the publish folder — zipping and SHA256SUMS stay on
# the host in build/publish.sh, so artifact names, layout and the checksum flow
# the in-app updater relies on never change.
#
# Container obj/bin are redirected with --artifacts-path into a gitignored folder
# so the container's linux-arm64 intermediates never clash with in-tree host
# builds; a plain host `dotnet build` keeps working afterwards untouched.

set -euo pipefail
cd "$(dirname "$0")"

# Pinned by digest so the verification gate is reproducible — a floating
# :10.0 tag can change SDK patch level between runs. To bump: pull the tag,
# then `docker image inspect --format '{{index .RepoDigests 0}}'` it.
IMAGE="${CC_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664}"
# Isolated, gitignored (under artifacts/) home for the container's obj/bin.
ARTIFACTS_PATH="artifacts/docker-build"

MODE="${1:-gate}"
case "$MODE" in
  gate)
    docker run --rm -v "$PWD":/src -w /src "$IMAGE" bash -c '
      set -e
      # Headless Avalonia + Skia in App.Tests needs fontconfig on the SDK image.
      apt-get update -qq && apt-get install -y --no-install-recommends fontconfig >/dev/null
      dotnet test  tests/CertConvert.Core.Tests --nologo -v q --artifacts-path '"$ARTIFACTS_PATH"'
      dotnet test  tests/CertConvert.App.Tests  --nologo -v q --artifacts-path '"$ARTIFACTS_PATH"'
      dotnet build src/CertConvert -c Release    --nologo -v q --artifacts-path '"$ARTIFACTS_PATH"'
      # The store variant (self-updater and Ko-fi stripped) must stay green too.
      dotnet build src/CertConvert -c Release -p:StoreBuild=true --nologo -v q --artifacts-path '"$ARTIFACTS_PATH"'
      echo "Gate passed — both suites green, Release + Store builds ok."
    '
    ;;
  win-x64)
    # Same properties as build/publish.sh; output folder is artifacts/win-x64 so
    # the host zip stays CertConvert-<version>-win-x64.zip with win-x64/ inside.
    docker run --rm -v "$PWD":/src -w /src "$IMAGE" bash -c '
      set -e
      dotnet publish src/CertConvert/CertConvert.csproj \
        -c Release \
        -r win-x64 \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:DebugType=none \
        --artifacts-path '"$ARTIFACTS_PATH"' \
        -o artifacts/win-x64
      echo "Published: artifacts/win-x64"
    '
    ;;
  *)
    echo "usage: $(basename "$0") [win-x64]" >&2
    exit 2
    ;;
esac
