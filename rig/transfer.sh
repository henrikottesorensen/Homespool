#!/usr/bin/env bash
#
# Upload a file to Homespool and tell a printer to fetch it - the first two of the three calls.
# The third (start/files, i.e. print it) is deliberately separate and printed at the end, because a
# transfer takes as long as it takes and a print starts instantly.
#
#   ./transfer.sh private-captures/G_0.4n_0.2mm_PLA_MK3.5_2h45m.gcode
#
# Run it directly rather than as `sh transfer.sh` - it wants bash, not POSIX sh.
#
# The password is prompted for, not passed in. Two reasons, both bitten already: zsh performs
# history expansion on '!' inside double quotes, and the rig's default password ends in one; and a
# password given on the command line lands in ~/.zsh_history. Set PASSWORD in the environment
# instead if you are scripting this, where neither applies.
set -euo pipefail

BASE="${BASE:-http://localhost:5052}"
UUID="${UUID:-3D3C8175-C6CB-4A02-8B78-E2CA9ED54FF6}"   # the MK3.5, printer id 2
TEAM="${TEAM:-1}"
EMAIL="${EMAIL:-rig@example.com}"
FILE="${1:?usage: transfer.sh <gcode file>}"

if [ -z "${PASSWORD:-}" ]; then
    read -rsp "password for $EMAIL: " PASSWORD
    echo
fi

JAR="$(mktemp)"
trap 'rm -f "$JAR"' EXIT

# Razor forms are antiforgery-protected, so each POST needs the token from the page carrying it.
# --data-urlencode rather than -d: the token is base64, and a '+' in a -d value arrives as a space.
form_token() {
    curl -sS -c "$JAR" -b "$JAR" "$BASE$1" \
        | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' \
        | head -1 | sed 's/.*value="\([^"]*\)".*/\1/'
}

echo "==> signing in as $EMAIL"
curl -sS -c "$JAR" -b "$JAR" -o /dev/null \
    --data-urlencode "__RequestVerificationToken=$(form_token /Account/Login)" \
    --data-urlencode "Input.Email=$EMAIL" \
    --data-urlencode "Input.Password=$PASSWORD" \
    --data-urlencode "Input.RememberMe=false" \
    "$BASE/Account/Login"

# Verify rather than assume. A failed Identity sign-in re-renders the page as 200 with a validation
# error, so the POST itself looks fine - the first honest signal is that the cookie does not
# authenticate. Checked here because carrying on produces a confusing JSON parse error two steps
# later, and each silent retry burns one of five attempts before the account locks out.
if [ "$(curl -sS -b "$JAR" -o /dev/null -w '%{http_code}' "$BASE/api/v1/user")" != "200" ]; then
    echo "    sign-in failed - wrong password, or the account is locked out." >&2
    echo "    Lockout is enabled: five failures locks it for a few minutes." >&2
    exit 1
fi
echo "    authenticated"

# The API is cookie-authenticated like the pages, so the same jar carries it.
NAME="$(basename "$FILE")"
echo "==> uploading $NAME ($(wc -c <"$FILE" | tr -d ' ') bytes)"
UPLOAD="$(curl -sS -b "$JAR" -T "$FILE" "$BASE/api/v1/files/$NAME")"
echo "    $UPLOAD"

read -r HASH PRINTER_PATH <<<"$(python3 -c '
import json,sys
try:
    d=json.loads(sys.argv[1])
except ValueError:
    sys.exit("upload did not return JSON - see the response above")
print(d["hash"], d["printerPath"])' "$UPLOAD")"

echo "==> telling the printer to fetch it"
curl -sS -b "$JAR" -X PUT "$BASE/api/v1/printers/$UUID/command/start/cloud" \
    -H 'Content-Type: application/json' \
    -d "{\"hash\":\"$HASH\",\"teamId\":$TEAM,\"printNow\":false}" \
    -w '    HTTP %{http_code}\n'

cat <<EOF

Transfer commanded. 204 means the printer accepted it (it answers TRANSFER_INFO, not FINISHED).
The bytes now move at the printer's pace - watch for TransferFinished in the log:

    tail -f logs/*.log | grep -i transfer

Then print it:

    curl -b <jar> -X PUT "$BASE/api/v1/printers/$UUID/command/start/files" \\
        -H 'Content-Type: application/json' \\
        -d '{"path":"$PRINTER_PATH","printNow":true}'
EOF
