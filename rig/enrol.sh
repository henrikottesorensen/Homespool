#!/usr/bin/env bash
#
# Enrol a printer identity against a running Homespool and write rig/identity.json.
#
#   ./rig/enrol.sh <setup-token>
#
# The setup token is printed once at startup by AdminBootstrap, held in memory only, and regenerated
# on every restart - so grab it from the server's log for this run.
#
# Does the whole first-run dance so a rig session needs no browser: create the administrator, sign
# in, register a printer, claim it, then poll for the issued token. Every step is the same HTTP a
# real client would make; nothing reaches into the database.
set -euo pipefail

TOKEN="${1:?usage: enrol.sh <setup-token>}"
BASE="${BASE:-http://localhost:5052}"
EMAIL="${EMAIL:-rig@example.com}"
PASSWORD="${PASSWORD:-Correct-Horse-Battery-Staple-1!}"
OUT="${OUT:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/identity.json}"
JAR="$(mktemp)"

# A 50-character fingerprint, as the firmware sends on /p/register; the WebSocket upgrade later
# presents its first 16 (notes/cross-channel-identity-bug.md).
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
    --data-urlencode "Input.Email=$EMAIL" \
    --data-urlencode "Input.Password=$PASSWORD" \
    --data-urlencode "Input.ConfirmPassword=$PASSWORD" \
    "$BASE/setup"

echo "==> signing in"
curl -sS -c "$JAR" -b "$JAR" -o /dev/null \
    --data-urlencode "__RequestVerificationToken=$(form_token /Account/Login)" \
    --data-urlencode "Input.Email=$EMAIL" \
    --data-urlencode "Input.Password=$PASSWORD" \
    --data-urlencode "Input.RememberMe=false" \
    "$BASE/Account/Login"

echo "==> registering the printer"
CODE="$(curl -sS -D - -o /dev/null -X POST "$BASE/p/register" \
    -H 'Content-Type: application/json' \
    -d "{\"sn\":\"$SERIAL\",\"fingerprint\":\"$FINGERPRINT\",\"printer_type\":\"1.3.5\",\"firmware\":\"6.6.0\"}" \
    | { grep -i '^Code:' || true; } | tr -d '\r' | awk '{print $2}')"

if [ -z "$CODE" ]; then
    echo "no claim code returned - is the server running at $BASE?" >&2
    exit 1
fi

echo "    code $CODE"

echo "==> claiming it"
curl -sS -c "$JAR" -b "$JAR" -o /dev/null -X POST "$BASE/api/v1/printers/register" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"Rig printer\",\"location\":\"Container\",\"code\":\"$CODE\"}"

echo "==> collecting the token"
PRINTER_TOKEN="$(curl -sS -D - -o /dev/null "$BASE/p/register" \
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
  "PrinterType": "1.3.5",
  "Firmware": "6.6.0",
  "Token": "$PRINTER_TOKEN"
}
EOF

rm -f "$JAR"
echo "==> wrote $OUT"
