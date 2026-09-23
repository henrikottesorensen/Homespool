#!/usr/bin/env bash
#
# The fairness rig: can one printer starve another? Two fake printers on one real server - a victim
# at a printer's pace, and an attacker that blasts - and what reaches the database from the victim
# before, during and after the blast. See tools/fairness/README.md for what it is for and what it
# found.
#
#   ./tools/fairness/fairness-rig.sh
#   BLAST_SECONDS=60 ./tools/fairness/fairness-rig.sh
#   AFTER_SECONDS=420 ./tools/fairness/fairness-rig.sh              # watch a dead sender's backlog
#   EXTRA_ENV="PrusaConnect__MessagesPerSecond=1000000 PrusaConnect__MessageBurst=1000000" \
#       ./tools/fairness/fairness-rig.sh                            # the budget out of reach
#
# Needs a Debug build (`dotnet build Homespool.slnx`), curl, sqlite3 and python3. Runs the server at
# Information - production log level - on the ordinary disk. Everything it starts is killed on exit,
# Ctrl-C included; artefacts land in $TMPDIR/homespool-fairness-rig.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TMP_BASE="${TMPDIR:-/tmp}"
RUN="${RUN:-${TMP_BASE%/}/homespool-fairness-rig}"
PORT="${PORT:-5099}"
BASE="http://127.0.0.1:$PORT"

# Plaintext, via PrusaConnect__PrinterTls below: the rig measures what one socket does to another,
# and a certificate the fake would have to be taught to trust adds a way to fail that has nothing
# to do with that.
PRINTER_PORT="${PRINTER_PORT:-15443}"
PRINTER_BASE="http://127.0.0.1:$PRINTER_PORT"
HOST_DLL="$ROOT/Homespool.Host/bin/Debug/net10.0/Homespool.Host.dll"
CLI="$ROOT/Homespool.FakePrinter.Cli/bin/Debug/net10.0/Homespool.FakePrinter.Cli.dll"

QUIET_SECONDS="${QUIET_SECONDS:-20}"
BLAST_SECONDS="${BLAST_SECONDS:-20}"
AFTER_SECONDS="${AFTER_SECONDS:-15}"

# The victim's pace. --events-every-seconds makes an event take the place of a telemetry tick, so
# 1000 ms and 2 s land half a sample and half an event a second. The quiet window measures what the
# victim actually lands; the other windows are read against it, not against these numbers.
VICTIM_INTERVAL_MS="${VICTIM_INTERVAL_MS:-1000}"
VICTIM_EVENT_SECONDS="${VICTIM_EVENT_SECONDS:-2}"

# Extra NAME=value settings for the server, space-separated, e.g. a budget to compare against.
EXTRA_ENV="${EXTRA_ENV:-}"

for dll in "$HOST_DLL" "$CLI"; do
    if [ ! -f "$dll" ]; then
        echo "missing $dll - run: dotnet build Homespool.slnx" >&2
        exit 1
    fi
done

for tool in curl sqlite3 python3; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "needs $tool on PATH" >&2
        exit 1
    fi
done

rm -rf "$RUN"
mkdir -p "$RUN"
DB="$RUN/Homespool.Sqlite"

SERVER_PID=""
VICTIM_PID=""
ATTACK_PID=""
ENROL_PID=""

cleanup() {
    for pid in $ATTACK_PID $VICTIM_PID $ENROL_PID $SERVER_PID; do
        kill -9 "$pid" 2>/dev/null
    done
}
trap cleanup EXIT INT TERM

now() {
    python3 -c 'import time; print(time.time())'
}

cd "$ROOT/Homespool.Host"
# Ports come from Listeners:*, not ASPNETCORE_URLS - Kestrel ignores that once endpoints are
# configured in code. $EXTRA_ENV is deliberately unquoted: it is a list of assignments.
# shellcheck disable=SC2086
env ASPNETCORE_ENVIRONMENT=Development \
    Listeners__UserPort="$PORT" \
    Listeners__PrinterPort="$PRINTER_PORT" \
    PrusaConnect__PrinterTls=false \
    Serilog__MinimumLevel__Default=Information \
    ConnectionStrings__HomespoolDb="Data Source=$DB" \
    $EXTRA_ENV \
    dotnet "$HOST_DLL" > "$RUN/server.log" 2>&1 &
SERVER_PID=$!

for _ in $(seq 1 60); do
    if curl -fsS -o /dev/null "$BASE/health/live" 2>/dev/null; then break; fi
    sleep 1
done

# First-run setup: the bootstrap token is logged as a CLEF property, not as a bare line. A setup
# that fails answers 200 with the form again; one that works redirects.
TOKEN="$(grep -o '"SetupToken":"[^"]*"' "$RUN/server.log" | head -1 | sed 's/.*:"//; s/"$//')"
AF="$(curl -fsS -c "$RUN/cookies" "$BASE/setup" \
    | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' \
    | sed 's/.*value="//; s/"$//' | head -1)"
SETUP_STATUS="$(curl -s -o /dev/null -w '%{http_code}' -b "$RUN/cookies" -c "$RUN/cookies" \
    --data-urlencode "__RequestVerificationToken=$AF" \
    --data-urlencode "Input.Email=admin@example.com" \
    --data-urlencode "Input.Username=admin" \
    --data-urlencode "Input.Password=Correct-Horse-Battery-Staple-1!" \
    --data-urlencode "Input.ConfirmPassword=Correct-Horse-Battery-Staple-1!" \
    --data-urlencode "Input.Token=$TOKEN" "$BASE/setup")"

if [ "$SETUP_STATUS" != "302" ]; then
    echo "### setup answered $SETUP_STATUS, not a redirect - its form has probably changed" >&2
    exit 1
fi

# Enrols a fake printer and claims it with the setup cookie. The claim API refuses a cookie write
# that does not say it came from the site's own pages, so the request says so.
enrol() {
    local name="$1"
    local code=""

    dotnet "$CLI" enrol --server "$PRINTER_BASE" --identity "$RUN/$name.json" > "$RUN/enrol-$name.log" 2>&1 &
    ENROL_PID=$!

    for _ in $(seq 1 30); do
        # `|| true`: until the CLI prints the code grep exits 1, and pipefail would kill the script.
        code="$(grep -o 'Claim code: .*' "$RUN/enrol-$name.log" 2>/dev/null | head -1 | sed 's/Claim code: //' | tr -d '\r' || true)"
        if [ -n "$code" ]; then break; fi
        sleep 1
    done

    curl -s -o "$RUN/claim-$name.json" -b "$RUN/cookies" \
        -H 'Sec-Fetch-Site: same-origin' -H 'Content-Type: application/json' \
        -d "{\"code\":\"$code\",\"name\":\"$name\",\"location\":\"loopback\"}" \
        "$BASE/api/v1/printers/register"

    # Without this the enrol CLI polls for a claim that is never coming, and the rig hangs.
    if ! grep -q '"uuid"' "$RUN/claim-$name.json"; then
        echo "### claiming $name failed: $(cat "$RUN/claim-$name.json")" >&2
        exit 1
    fi

    wait "$ENROL_PID"
    ENROL_PID=""
}

enrol victim
enrol attacker

VICTIM_ID="$(sqlite3 "$DB" "select Id from Printers where Name='victim';")"
ATTACK_ID="$(sqlite3 "$DB" "select Id from Printers where Name='attacker';")"
echo "### victim is printer $VICTIM_ID, attacker is printer $ATTACK_ID"

dotnet "$CLI" run --server "$PRINTER_BASE" --identity "$RUN/victim.json" --printing \
    --interval-ms "$VICTIM_INTERVAL_MS" --events-every-seconds "$VICTIM_EVENT_SECONDS" > "$RUN/victim.log" 2>&1 &
VICTIM_PID=$!

# A few seconds for the victim to connect and settle before the quiet window starts counting.
sleep 3
T_QUIET="$(now)"
sleep "$QUIET_SECONDS"

T_BLAST="$(now)"
echo "### blasting for ${BLAST_SECONDS}s"
dotnet "$CLI" blast --server "$PRINTER_BASE" --identity "$RUN/attacker.json" --printing > "$RUN/attacker.log" 2>&1 &
ATTACK_PID=$!
sleep "$BLAST_SECONDS"

kill -INT "$ATTACK_PID" 2>/dev/null
T_STOP="$(now)"
for _ in $(seq 1 25); do
    if ! kill -0 "$ATTACK_PID" 2>/dev/null; then break; fi
    sleep 0.2
done
kill -9 "$ATTACK_PID" 2>/dev/null
# Reaped so bash does not print its own "Killed: 9" job notice, which reads like a rig failure.
wait "$ATTACK_PID" 2>/dev/null
ATTACK_PID=""

sleep "$AFTER_SECONDS"
T_END="$(now)"

# With the setup cookie: the counters are in the report, which only an administrator is shown.
curl -s --max-time 2 -b "$RUN/cookies" "$BASE/health" > "$RUN/health.json"

kill -INT "$VICTIM_PID" 2>/dev/null
sleep 1
kill -9 "$VICTIM_PID" 2>/dev/null
wait "$VICTIM_PID" 2>/dev/null
VICTIM_PID=""

python3 - "$SERVER_PID" <<'PY'
import os, signal, sys, time
pid = int(sys.argv[1])
start = time.perf_counter()
os.kill(pid, signal.SIGTERM)
while True:
    try:
        os.kill(pid, 0)
    except OSError:
        break
    if time.perf_counter() - start > 60:
        print("### STILL RUNNING after 60s")
        break
    time.sleep(0.005)
print(f"### SIGTERM -> exit in {(time.perf_counter() - start) * 1000:.0f} ms")
PY
SERVER_PID=""

echo "### per printer and window, from each row's stored Timestamp:"
python3 - "$DB" "$T_QUIET" "$T_BLAST" "$T_STOP" "$T_END" "$VICTIM_ID" "$ATTACK_ID" "$RUN/health.json" <<'PY'
import json, sqlite3, sys

db, tq, tb, ts, te, victim, attacker, health = sys.argv[1:]
tq, tb, ts, te = map(float, (tq, tb, ts, te))
con = sqlite3.connect(db)

def epoch(value):
    # Stored as epoch milliseconds; tolerate seconds in case that ever changes.
    return value / 1000.0 if value > 1e11 else float(value)

def stamps(table, printer):
    return [epoch(r[0]) for r in con.execute(f"select Timestamp from {table} where PrinterId = ?", (int(printer),))]

windows = [("quiet", tq, tb), ("blast", tb, ts), ("after", ts, te)]
rates = {}
print(f"  {'':10}{'window':>8}{'secs':>8}{'samples':>10}{'/s':>10}{'events':>8}{'/s':>8}")
for who, printer in (("victim", victim), ("attacker", attacker)):
    samples, events = stamps("TelemetrySamples", printer), stamps("PrinterEvents", printer)
    for name, start, end in windows:
        n_s = sum(start <= t < end for t in samples)
        n_e = sum(start <= t < end for t in events)
        span = end - start
        rates[(who, name)] = (n_s / span, n_e / span)
        print(f"  {who:10}{name:>8}{span:8.1f}{n_s:10}{n_s / span:10.2f}{n_e:8}{n_e / span:8.2f}")

(qs, qe), (bs, be) = rates[("victim", "quiet")], rates[("victim", "blast")]
if qs and qe:
    print(f"### the victim kept {bs / qs:.0%} of its samples and {be / qe:.0%} of its events through the blast")

try:
    with open(health) as f:
        report = json.load(f)
    data = next(c for c in report["checks"] if c["name"] == "telemetry-persistence")["data"]
    print(f"### writer: dropped={data['droppedMessages']} discardedEvents={data['discardedEvents']} "
          f"pendingSamples={data['pendingSamples']} pendingEvents={data['pendingEvents']}")
except Exception as error:
    print(f"### health report unreadable: {error}")

print(f"### integrity_check: {con.execute('pragma integrity_check').fetchone()[0]}")
PY

echo "### warnings and errors after startup, by message, with the latest instance:"
python3 - "$RUN/server.log" "$T_QUIET" <<'PY'
import collections, json, sys
from datetime import datetime

path, since = sys.argv[1], float(sys.argv[2])
counts, latest = collections.Counter(), {}
with open(path, errors="replace") as f:
    for line in f:
        try:
            e = json.loads(line)
        except Exception:
            continue
        if e.get("@l") not in ("Warning", "Error", "Fatal"):
            continue
        if datetime.fromisoformat(e["@t"].replace("Z", "+00:00")).timestamp() < since:
            continue
        key = (e["@l"], e.get("@i", ""))
        counts[key] += 1
        latest[key] = e.get("@m", "")
if not counts:
    print("  none")
for (level, event), n in counts.most_common(12):
    print(f"  {n:>6}  {level:<7} {latest[(level, event)][:160]}")
PY

echo "### artefacts in $RUN (server.log, victim.log, attacker.log, health.json, the database)"
