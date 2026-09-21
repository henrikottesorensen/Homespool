#!/usr/bin/env bash
#
# Enrol a printer identity against a running Homespool and write rig/identity.json.
#
#   ./rig/enrol.sh <setup-token>
#
#   PRINTER_TYPE=3.1.0 OUT=rig/identity-xl.json ./rig/enrol.sh <setup-token>   # a second, XL printer
#
# The setup token is printed once at startup by AdminBootstrap, held in memory only, and regenerated
# on every restart - so grab it from the server's log for this run.
#
# One identity is one printer, so a rig pretending to be a different machine needs its own - hence
# OUT. PRINTER_TYPE is what /p/register records; it should match the printer the rig was BUILT as,
# because connect_rig sends its build's own version in INFO (get_printer_version()) and a server
# would otherwise see the two disagree. 1.3.5 is MK3.5, 2.1.0 MINI, 3.1.0 XL - the table is
# include/common/printer_model_data.hpp.
#
# Does the whole first-run dance so a rig session needs no browser: create the administrator, sign
# in, prove the password again, mint an API token, register a printer, claim it, then poll for the
# issued token. Every step is the same HTTP a real client would make; nothing reaches into the
# database.
#
# Also writes rig/api-token - a personal access token for this account, so that any *later* script
# can call /api/v1 with a single `Authorization: Bearer` header instead of repeating the sign-in and
# antiforgery dance below. This script still has to do that dance itself: it
# starts from an empty server where no account, and therefore no token, exists yet.
#
# The account it creates is the server's administrator, so its password is not written in this file.
# It is PASSWORD if set, otherwise rig/password, which the first run generates. sdk-claim.py shares
# that file.
#
# BASE and PRINTER_BASE must be loopback addresses. Running this against a real server would give it
# an administrator whose password sits in a file on this machine and crosses the network over plain
# HTTP. RIG_ALLOW_REMOTE=1 lifts the check when that is really what you want.
set -euo pipefail

TOKEN="${1:?usage: enrol.sh <setup-token>}"
BASE="${BASE:-http://localhost:5052}"

# /p/* lives on the printer listener and on no other, so the two registration calls below go
# somewhere different from the account and API calls. Plain HTTP, and for a rig running the app
# directly that is now simply what the listener is: the app serves no TLS on any port, because nginx
# terminates the printer's in front of it in the shipped stack. PrusaConnect__PrinterTls=false is
# still the right setting for a rig - it stops a certificate being minted and writes tls = false into
# any ini - but it is no longer what makes this line work.
#
# There is no TLS path to point this at any more. To exercise one, put the shipped proxy in front:
# `docker compose up` and connect to its published printer port instead.
PRINTER_BASE="${PRINTER_BASE:-http://localhost:15443}"
PRINTER_TYPE="${PRINTER_TYPE:-1.3.5}"
# An account is identified by one name, and sign-in takes it in a single Input.Login field that
# accepts either the username or the email. Both are needed here: /setup
# asks for a username, and posting Input.Email to the login form silently signs nobody in.
USERNAME="${USERNAME:-rig}"
EMAIL="${EMAIL:-rig@example.com}"
RIG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="${OUT:-$RIG_DIR/identity.json}"
API_TOKEN_OUT="${API_TOKEN_OUT:-$RIG_DIR/api-token}"
PASSWORD_FILE="${PASSWORD_FILE:-$RIG_DIR/password}"

require_loopback() {
    if [ "${RIG_ALLOW_REMOTE:-}" = 1 ]; then
        return
    fi

    # A URL with no scheme has no hostname to parse and is refused rather than guessed at.
    if ! python3 -c '
import ipaddress, sys, urllib.parse
host = urllib.parse.urlsplit(sys.argv[1]).hostname or ""
try:
    loopback = ipaddress.ip_address(host).is_loopback
except ValueError:
    loopback = host == "localhost"
sys.exit(0 if loopback else 1)' "$2"; then
        echo "$1=$2 is not a loopback address. Set RIG_ALLOW_REMOTE=1 to enrol against it anyway." >&2
        exit 1
    fi
}

require_loopback BASE "$BASE"
require_loopback PRINTER_BASE "$PRINTER_BASE"

if [ -z "${PASSWORD:-}" ]; then
    if [ ! -e "$PASSWORD_FILE" ]; then
        # Identity's composition rules are still on, so a draw is kept only if it has an upper, a
        # lower, a digit and a symbol. The URL-safe alphabet has no '!' for a shell to expand.
        # O_EXCL and 0600 at creation, so the file is never briefly readable and never overwritten.
        python3 - "$PASSWORD_FILE" <<'PY'
import os, re, secrets, sys
while True:
    password = secrets.token_urlsafe(24)
    if all(re.search(c, password) for c in ("[A-Z]", "[a-z]", "[0-9]", "[-_]")):
        break
fd = os.open(sys.argv[1], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, "w") as f:
    f.write(password + "\n")
PY
        echo "==> generated a password for $USERNAME in $PASSWORD_FILE"
    fi

    PASSWORD="$(cat "$PASSWORD_FILE")"

    if [ -z "$PASSWORD" ]; then
        echo "$PASSWORD_FILE is empty - delete it to generate a new one, or set PASSWORD." >&2
        exit 1
    fi
fi

JAR="$(mktemp)"

# A 50-character fingerprint, as the firmware sends on /p/register; the WebSocket upgrade later
# presents its first 16.
# Not `tr </dev/urandom | head -c`: head closes the pipe, tr dies of SIGPIPE, and `set -o pipefail`
# then fails the script with no output at all.
FINGERPRINT="$(python3 -c "import random,string;print(''.join(random.choices(string.ascii_uppercase+string.digits,k=50)))")"
SERIAL="RIG-$(python3 -c "import random,string;print(''.join(random.choices(string.ascii_uppercase+string.digits,k=12)))")"

# Razor forms are antiforgery-protected, so each POST needs the token from the page that carries it.
# Every value goes through --data-urlencode rather than -d: the antiforgery and setup tokens are
# base64, and a '+' in a -d value arrives at the server as a space.
form_token() {
    curl -sS -c "$JAR" -b "$JAR" "$BASE$1" \
        | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' \
        | head -1 | sed 's/.*value="\([^"]*\)".*/\1/'
}

echo "==> creating the administrator"
curl -sS -c "$JAR" -b "$JAR" -o /dev/null \
    --data-urlencode "__RequestVerificationToken=$(form_token /setup)" \
    --data-urlencode "Input.Token=$TOKEN" \
    --data-urlencode "Input.Username=$USERNAME" \
    --data-urlencode "Input.Email=$EMAIL" \
    --data-urlencode "Input.Password=$PASSWORD" \
    --data-urlencode "Input.ConfirmPassword=$PASSWORD" \
    "$BASE/setup"

echo "==> signing in"
curl -sS -c "$JAR" -b "$JAR" -o /dev/null \
    --data-urlencode "__RequestVerificationToken=$(form_token /Account/Login)" \
    --data-urlencode "Input.Login=$USERNAME" \
    --data-urlencode "Input.Password=$PASSWORD" \
    --data-urlencode "Input.RememberMe=false" \
    "$BASE/Account/Login"

# A token is scoped now, and a scopeless post is refused rather than defaulted.
# Every capability, because this token exists for whatever script
# comes next rather than for a known job; narrow it at the call site if that ever matters.
SCOPE_ARGS=""
for scope in ViewPrinter ControlPrinter ManagePrinter Print ViewQueue ViewHistory \
             ViewOwnFiles UploadOwnFiles ManipulateOwnFiles ViewCamera ManageCamera ViewAccountDetails; do
    SCOPE_ARGS="$SCOPE_ARGS --data-urlencode Input.Scope=$scope"
done

echo "==> proving the password again"
# The token page wants a recent proof, which a sign-in does not give: a browser is redirected to
# Account/Reauthenticate and back. Success is that redirect back. A refused password re-renders the
# page, and a session that never signed in is redirected to the login page instead.
PROVED_AT="$(curl -sS -c "$JAR" -b "$JAR" -o /dev/null -w '%{redirect_url}' \
    --data-urlencode "__RequestVerificationToken=$(form_token /Account/Reauthenticate)" \
    --data-urlencode "Input.Password=$PASSWORD" \
    "$BASE/Account/Reauthenticate?returnUrl=%2FAccount%2FManage%2FApiTokens")"

case "$PROVED_AT" in
    */Account/Manage/ApiTokens) ;;
    *)
        echo "the password was not accepted - was the server fresh? An existing administrator only" >&2
        echo "signs in with the password it was created with." >&2
        exit 1
        ;;
esac

echo "==> minting an API token"
# The one-time secret is rendered into the page that creates it and never stored, so it is scraped
# from that response rather than fetched afterwards - there is no afterwards. `|| true` because a
# grep that matches nothing fails the pipeline, and pipefail would end the script here with no word
# of why; the empty check below is what says so.
API_TOKEN="$(curl -sS -c "$JAR" -b "$JAR" \
    --data-urlencode "__RequestVerificationToken=$(form_token /Account/Manage/ApiTokens)" \
    --data-urlencode "Input.Name=rig" \
    $SCOPE_ARGS \
    "$BASE/Account/Manage/ApiTokens" \
    | { grep -o '<code id="created-token">[^<]*</code>' || true; } \
    | sed 's/.*>\(.*\)<.*/\1/')"

if [ -z "$API_TOKEN" ]; then
    echo "no API token was issued - is /Account/Manage/ApiTokens reachable?" >&2
    exit 1
fi

echo "==> registering the printer"
CODE="$(curl -sS -D - -o /dev/null -X POST "$PRINTER_BASE/p/register" \
    -H 'Content-Type: application/json' \
    -d "{\"sn\":\"$SERIAL\",\"fingerprint\":\"$FINGERPRINT\",\"printer_type\":\"$PRINTER_TYPE\",\"firmware\":\"6.6.0\"}" \
    | { grep -i '^Code:' || true; } | tr -d '\r' | awk '{print $2}')"

if [ -z "$CODE" ]; then
    echo "no claim code returned - is the server running, with its printer listener at $PRINTER_BASE?" >&2
    exit 1
fi

echo "    code $CODE"

echo "==> claiming it"
# No cookie jar here, deliberately: this is the token doing the work, which is also the check that
# the token actually authenticates against a real API endpoint.
curl -sS -o /dev/null -X POST "$BASE/api/v1/printers/register" \
    -H "Authorization: Bearer $API_TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"Rig printer\",\"location\":\"Container\",\"code\":\"$CODE\"}"

echo "==> collecting the token"
PRINTER_TOKEN="$(curl -sS -D - -o /dev/null "$PRINTER_BASE/p/register" \
    -H "Code: $CODE" -H "Fingerprint: $FINGERPRINT" \
    | { grep -i '^Token:' || true; } | tr -d '\r' | awk '{print $2}')"

if [ -z "$PRINTER_TOKEN" ]; then
    echo "claim did not yield a token - the code may not have been redeemed" >&2
    exit 1
fi

cat > "$OUT" <<EOF
{
  "Fingerprint": "$FINGERPRINT",
  "SerialNumber": "$SERIAL",
  "PrinterType": "$PRINTER_TYPE",
  "Firmware": "6.6.0",
  "Token": "$PRINTER_TOKEN"
}
EOF

printf '%s\n' "$API_TOKEN" > "$API_TOKEN_OUT"
chmod 600 "$API_TOKEN_OUT"

rm -f "$JAR"
echo "==> wrote $OUT"
echo "==> wrote $API_TOKEN_OUT - use it as: curl -H \"Authorization: Bearer \$(cat $API_TOKEN_OUT)\" $BASE/api/v1/printers"
