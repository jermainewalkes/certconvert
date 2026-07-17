#!/usr/bin/env bash
# build-on-windows.sh — produce CertConvert's Store MSIX on the Windows build VM.
#
#   ./build-on-windows.sh
#
# The fast, cross-platform verification + win-x64 publish stays on
# build-in-docker.sh. This wrapper covers the ONE thing containers can't: the
# Store .msix, which needs the Windows SDK's MakeAppx.exe (build/make-msix.ps1).
#
# File channel is tar-over-SSH (VMware has no shared folders for ARM Windows
# guests): the working tree — incl. uncommitted edits — is streamed to the VM's
# local disk, built there, and the artifact streamed back. See NewLaptop/WINDOWS-VM.md.

set -euo pipefail
cd "$(dirname "$0")"
NL="$HOME/Claude/NewLaptop"
PROJ="CertConvert"
DST="C:/build/$PROJ"

"$NL/winvm.sh" start
"$NL/winvm.sh" wait-ssh

echo "== sync working tree -> VM ($DST)"
ssh winbuild "Remove-Item -Recurse -Force 'C:\\build\\$PROJ' -EA SilentlyContinue; New-Item -Force -ItemType Directory 'C:\\build\\$PROJ' | Out-Null"
# COPYFILE_DISABLE stops macOS tar emitting AppleDouble ._* sidecars, which
# Windows would extract as real files and the C# compiler would choke on.
COPYFILE_DISABLE=1 tar czf - --exclude='./.git' --exclude='*/bin' --exclude='*/obj' --exclude='./artifacts' \
          --exclude='./.dart_tool' --exclude='*/node_modules' -C "$PWD" . \
  | ssh winbuild "tar -xzf - -C $DST"

echo "== build MSIX on the VM"
ssh winbuild "cd 'C:\\build\\$PROJ'; .\\build\\make-msix.ps1"

echo "== pull artifact back"
mkdir -p artifacts/msix
ssh winbuild "tar -czf - -C $DST artifacts/msix" | tar -xzf - -C "$PWD"

echo "== MSIX back on the Mac:"
ls -la artifacts/msix/*.msix 2>/dev/null || { echo 'no .msix produced — check the build output above' >&2; exit 1; }
