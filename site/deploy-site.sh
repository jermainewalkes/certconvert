#!/usr/bin/env bash
#
# deploy-site.sh -- upload this site/ folder to the Fasthosts web root over FTPS.
#
# Credentials are read by curl from a DEDICATED netrc file, ~/.netrc-certconvert (NOT the
# shared ~/.netrc), so nothing secret is stored here or passed on the command line.
#
# One-time setup:
#   printf 'machine ftp.fasthosts.co.uk login YOURUSER password YOURPASS\n' > ~/.netrc-certconvert
#   chmod 600 ~/.netrc-certconvert
#
# Usage:
#   ./deploy-site.sh                          # upload to the web root (FTP login home)
#   CC_HOST=ftp.fasthosts.co.uk ./deploy-site.sh
#   CC_REMOTE_DIR=subdir ./deploy-site.sh     # into a sub-folder of the web root
#   CC_DRYRUN=1 ./deploy-site.sh              # list what WOULD upload, transfer nothing
#
# Fasthosts shared hosting lands you directly in the web root (no /htdocs), so
# CC_REMOTE_DIR is usually empty.
set -euo pipefail

HOST="${CC_HOST:-ftp.fasthosts.co.uk}"
REMOTE_DIR="${CC_REMOTE_DIR:-}"                 # empty = FTP login home (the web root on Fasthosts)
NETRC="$HOME/.netrc-certconvert"
SITE="$(cd "$(dirname "$0")" && pwd)"

[ -f "$NETRC" ] || {
  echo "ERROR: $NETRC not found." >&2
  echo "Create it (and chmod 600) with your Fasthosts FTP login:" >&2
  echo "  printf 'machine ${HOST} login YOURUSER password YOURPASS\\n' > \"$NETRC\"" >&2
  echo "  chmod 600 \"$NETRC\"" >&2
  exit 1
}
command -v curl >/dev/null 2>&1 || { echo "curl not found" >&2; exit 1; }

base="ftp://${HOST}/"
[ -n "$REMOTE_DIR" ] && base="ftp://${HOST}/${REMOTE_DIR#/}/"

echo "Deploying site/ -> ${base} (FTPS, dryrun=${CC_DRYRUN:-0})"
fail=0
cd "$SITE"
while IFS= read -r f; do
  rel="${f#./}"
  # Never upload local-only or secret files.
  case "$rel" in
    .DS_Store|*/.DS_Store) continue;;
    .gitignore) continue;;
    deploy-site.sh) continue;;
    mail.config.php) continue;;   # live SMTP config: uploaded manually, never from here
  esac
  if [ "${CC_DRYRUN:-0}" = "1" ]; then
    echo "  would upload  $rel"
    continue
  fi
  # --disable-epsv (plain PASV is more NAT/firewall-tolerant) + retries: Fasthosts' FTPS data
  # channel intermittently drops mid-transfer on larger files (curl error 56), so retry.
  if curl --netrc-file "$NETRC" --ssl-reqd --disable-epsv --ftp-create-dirs \
          --retry 8 --retry-all-errors --retry-delay 2 \
          --connect-timeout 30 --max-time 240 \
          -fsS -T "$f" "${base}${rel}"; then
    echo "  ok    $rel"
  else
    echo "  FAIL  $rel"; fail=1
  fi
done < <(find . -type f | sort)

if [ "${CC_DRYRUN:-0}" != "1" ]; then
  # Best-effort: remove the Fasthosts default placeholder so index.html is served.
  curl --netrc-file "$NETRC" --ssl-reqd -sS "${base}" -Q "DELE _index.htm" >/dev/null 2>&1 \
    && echo "  removed Fasthosts placeholder _index.htm" || true
fi

[ "$fail" = 0 ] && echo "Done." || { echo "Some uploads failed." >&2; exit 1; }
