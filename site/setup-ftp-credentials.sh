#!/usr/bin/env bash
# setup-ftp-credentials.sh — interactively create ~/.netrc-certconvert for
# deploy-site.sh, so the Fasthosts FTP login is never typed on a command line
# or stored in the repo. Run once (re-run any time to update the password).
#
#   ./setup-ftp-credentials.sh
#
# The file it writes is chmod 600 and read only by `curl --netrc-file`.
set -euo pipefail

NETRC="$HOME/.netrc-certconvert"
DEFAULT_HOST="ftp.fasthosts.co.uk"

echo "CertConvert site deploy — FTP credential setup"
echo "These go into $NETRC (permissions 600) and are used only by deploy-site.sh."
echo

if [ -f "$NETRC" ]; then
    printf "%s already exists. Overwrite it? [y/N] " "$NETRC"
    read -r reply
    case "$reply" in
        y|Y|yes|YES) ;;
        *) echo "Left unchanged. Nothing written."; exit 0 ;;
    esac
fi

printf "FTP host [%s]: " "$DEFAULT_HOST"
read -r host
host="${host:-$DEFAULT_HOST}"

printf "FTP username: "
read -r user
if [ -z "$user" ]; then
    echo "Username cannot be empty." >&2
    exit 1
fi

# -s hides the password; prompt twice to catch typos.
printf "FTP password: "
read -rs pass1; echo
printf "Confirm password: "
read -rs pass2; echo
if [ "$pass1" != "$pass2" ]; then
    echo "Passwords did not match. Nothing written." >&2
    exit 1
fi
if [ -z "$pass1" ]; then
    echo "Password cannot be empty." >&2
    exit 1
fi

# Write atomically with tight permissions from the outset.
umask 177
tmp="$(mktemp "${NETRC}.XXXXXX")"
printf 'machine %s login %s password %s\n' "$host" "$user" "$pass1" > "$tmp"
mv "$tmp" "$NETRC"
chmod 600 "$NETRC"
unset pass1 pass2

echo
echo "Wrote $NETRC (host $host, user $user)."
echo "Deploy the site with:  cd site && ./deploy-site.sh"
echo "Preview first with:    cd site && CC_DRYRUN=1 ./deploy-site.sh"

if command -v curl >/dev/null 2>&1; then
    printf "\nTest the login now? [y/N] "
    read -r test
    case "$test" in
        y|Y|yes|YES)
            if curl --netrc-file "$NETRC" --ssl-reqd --connect-timeout 15 \
                    -s -o /dev/null "ftp://${host}/"; then
                echo "Login OK — credentials work."
            else
                echo "Login test failed (rc $?). Check the username/password and try again." >&2
            fi
            ;;
    esac
fi
