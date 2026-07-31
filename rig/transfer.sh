#!/usr/bin/env bash
#
# Upload a file to Homespool and tell a printer to fetch it - the first two of the three calls.
# The third (print it) is deliberately separate and printed at the end, because a transfer takes as
# long as it takes and a print starts instantly.
#
#   ./rig/transfer.sh private-captures/G_0.4n_0.2mm_PLA_MK3.5_2h45m.gcode
#
# Run it directly rather than as `sh transfer.sh` - it wants bash, not POSIX sh.
#
# Authenticates with a personal access token (notes/api-tokens.md), read from rig/api-token, which
# `enrol.sh` writes. Override with TOKEN, or make one at /Account/Manage/ApiTokens.
#
# UUID below is a *default and it goes stale*: it names whichever printer was enrolled when this was
# last used, and the pre-release migration is regenerated in place, so any schema change empties the
# database and mints new ones. Pass UUID=... or take it from GET /api/v1/printers.
set -euo pipefail

BASE="${BASE:-http://localhost:5052}"
UUID="${UUID:-3D3C8175-C6CB-4A02-8B78-E2CA9ED54FF6}"   # the MK3.5 as of 2026-07-27, printer id 2
TEAM="${TEAM:-1}"
RIG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOKEN_FILE="${TOKEN_FILE:-$RIG_DIR/api-token}"
FILE="${1:?usage: transfer.sh <gcode file>}"

if [ -z "${TOKEN:-}" ]; then
    if [ ! -f "$TOKEN_FILE" ]; then
        echo "no token: expected $TOKEN_FILE (written by enrol.sh), or set TOKEN." >&2
        exit 1
    fi

    TOKEN="$(cat "$TOKEN_FILE")"
fi

AUTH="Authorization: Bearer $TOKEN"

# What this replaced, and why it is worth the note: the whole sign-in half of this script is gone -
# no cookie jar, no scraping __RequestVerificationToken off the login page, no password prompt (the
# rig password ends in '!', which zsh history-expands inside double quotes), and no five-attempt
# lockout to blunder into. One header does it.

# Verify rather than assume. A wrong token is a clean 401 here, where carrying on would produce a
# confusing JSON parse error two steps later.
if [ "$(curl -sS -H "$AUTH" -o /dev/null -w '%{http_code}' "$BASE/api/v1/user")" != "200" ]; then
    echo "    token rejected - it may have been revoked, or belong to another server." >&2
    exit 1
fi
echo "==> authenticated"

NAME="$(basename "$FILE")"
echo "==> uploading $NAME ($(wc -c <"$FILE" | tr -d ' ') bytes)"

# ?overwrite=true because a rig run is normally the same file again: without it a second run is a
# 409, which is the right default for a person and the wrong one for a script.
UPLOAD="$(curl -sS -H "$AUTH" -T "$FILE" "$BASE/api/v1/files/$NAME?overwrite=true")"
echo "    $UPLOAD"

read -r PRINTER_PATH <<<"$(python3 -c '
import json,sys
try:
    d=json.loads(sys.argv[1])
except ValueError:
    sys.exit("upload did not return JSON - see the response above")
print(d["printerPath"])' "$UPLOAD")"

echo "==> telling the printer to fetch it"
curl -sS -H "$AUTH" -X POST "$BASE/api/v1/printers/$UUID/files" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$NAME\"}" \
    -w '    HTTP %{http_code}\n'

cat <<EOF

Transfer commanded. 204 means the printer accepted it (it answers TRANSFER_INFO, not FINISHED).
The bytes now move at the printer's pace - watch for TransferFinished in the log:

    tail -f logs/*.log | grep -i transfer

Then print it:

    curl -H "Authorization: Bearer \$(cat $TOKEN_FILE)" \\
        -X POST "$BASE/api/v1/printers/$UUID/print" \\
        -H 'Content-Type: application/json' \\
        -d '{"path":"$PRINTER_PATH"}'
EOF
