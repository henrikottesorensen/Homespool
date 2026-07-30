#!/usr/bin/env bash
#
# The slow-database rig: reaches TelemetryWriter's failure path by making flushes *fail*, rather
# than by making the client fast. See tools/slow-db/README.md for what it is for and what it found;
# the full record is in notes/fake-printer-harness.md, "Two rigs, 2026-07-29".
#
#   ./tools/slow-db/slow-db-rig.sh                       # 90 s outage, full-disk mechanism
#   STALL_SECONDS=180 WRITE_BATCH_SIZE=50 ./tools/slow-db/slow-db-rig.sh
#   MECHANISM=lock ./tools/slow-db/slow-db-rig.sh        # the one that does NOT work; see below
#
# Needs a Debug build (`dotnet build Homespool.slnx`) and, for the default mechanism, macOS:
# hdiutil/diskutil provide the RAM disk. Everything it creates is torn down on exit, including on
# Ctrl-C.
#
# MECHANISM=full (default) - the database lives on a small RAM disk which is then filled, so every
#   flush fails immediately with SQLITE_FULL. This is the shape the buffer ceilings exist for:
#   repeated, instant rejection, so the drain loop keeps ingesting while nothing can be written.
#   Realistic too - a volume filling up is what an unthrottled log does to its own disk.
#
# MECHANISM=lock - an outside connection holds BEGIN IMMEDIATE. Kept because it documents a real and
#   *different* result: Microsoft.Data.Sqlite retries SQLITE_BUSY internally up to its command
#   timeout (30 s default), so a lock makes flushes BLOCK rather than fail. The buffers never grow,
#   the channel sheds instead, and health's pending counts freeze at their last published value
#   (PublishHealth only runs in SafeFlushAsync's finally). Measured 2026-07-29: 15 s lock, 10,567
#   dropped, 0 pending, 0 errors. Reach for it to demonstrate that difference, not to fill buffers.
#
# Runs the server at Information - production log level. The dev-only Verbose firehose would bury
# the log-volume question this rig asks. The server log stays on the ordinary disk, never on the RAM
# disk, so filling the database's volume cannot silence the evidence.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TMP_BASE="${TMPDIR:-/tmp}"
RUN="${RUN:-${TMP_BASE%/}/homespool-slow-db-rig}"
PORT="${PORT:-5099}"
BASE="http://127.0.0.1:$PORT"

# The printer protocol lives on its own listener and exists nowhere else, so the fake printer gets a
# different address from the API calls above it. Plaintext, via PrusaConnect__PrinterTls below: this
# rig measures the write path under a stalled database, and making it also carry a certificate the
# fake would have to be taught to trust adds a way for the rig to fail that has nothing to do with
# what it measures.
PRINTER_PORT="${PRINTER_PORT:-15443}"
PRINTER_BASE="http://127.0.0.1:$PRINTER_PORT"
HOST_DLL="$ROOT/Homespool.Host/bin/Debug/net10.0/Homespool.Host.dll"
CLI="$ROOT/Homespool.FakePrinter.Cli/bin/Debug/net10.0/Homespool.FakePrinter.Cli.dll"

MECHANISM="${MECHANISM:-full}"
VOLUME="${VOLUME:-/Volumes/HomespoolStallDisk}"
RAMDISK_SECTORS="${RAMDISK_SECTORS:-262144}"   # 512-byte sectors: 262144 = 128 MB
EVENTS_EVERY="${EVENTS_EVERY:-10}"
INTERVAL_MS="${INTERVAL_MS:-1}"
STALL_SECONDS="${STALL_SECONDS:-90}"
WARMUP_SECONDS="${WARMUP_SECONDS:-5}"
RECOVERY_SECONDS="${RECOVERY_SECONDS:-20}"
LOG_LIMIT_MB="${LOG_LIMIT_MB:-2048}"

# 0 keeps the outage in place through SIGTERM, which is the only way to measure what a shutdown
# costs while the database is still refusing writes. The default ends the outage first, so the
# shutdown measured is the ordinary healthy one.
FREE_BEFORE_SHUTDOWN="${FREE_BEFORE_SHUTDOWN:-1}"

# The ceilings are WriteBatchSize * 20 (samples) and * 10 (events). Lowering the batch size scales
# both together, so the 2:1 cap ratio and the 10:1 stream ratio - the two things the ordering claim
# rests on - are untouched, while the caps come within reach of a short run instead of needing a
# quarter-hour outage. 50 gives caps of 1,000 samples and 500 events.
WRITE_BATCH_SIZE="${WRITE_BATCH_SIZE:-500}"

for dll in "$HOST_DLL" "$CLI"; do
    if [ ! -f "$dll" ]; then
        echo "missing $dll - run: dotnet build Homespool.slnx" >&2
        exit 1
    fi
done

if [ "$MECHANISM" = "full" ] && ! command -v hdiutil >/dev/null 2>&1; then
    echo "MECHANISM=full needs macOS (hdiutil/diskutil) for the RAM disk." >&2
    echo "On Linux, mount a small tmpfs at \$VOLUME by hand and use MECHANISM=premounted." >&2
    exit 1
fi

rm -rf "$RUN"
mkdir -p "$RUN"

SERVER_PID=""
LOAD_PID=""
LOCK_PID=""
RAMDISK_DEV=""

cleanup() {
    [ -n "$LOAD_PID" ] && kill -9 "$LOAD_PID" 2>/dev/null
    [ -n "$LOCK_PID" ] && kill -9 "$LOCK_PID" 2>/dev/null
    [ -n "$SERVER_PID" ] && kill -9 "$SERVER_PID" 2>/dev/null

    if [ -n "$RAMDISK_DEV" ]; then
        hdiutil detach "$RAMDISK_DEV" -force >/dev/null 2>&1
        echo "### ram disk $RAMDISK_DEV detached"
    fi
}
trap cleanup EXIT INT TERM

case "$MECHANISM" in
    full)
        LABEL="$(basename "$VOLUME")"

        # Unmount any leftover volume of this name first. Without it macOS mounts the new one as
        # "<name> 1" and $VOLUME silently points at the *previous* run's disk.
        while [ -d "$VOLUME" ]; do
            diskutil unmount force "$VOLUME" >/dev/null 2>&1 || break
            sleep 0.5
        done

        RAMDISK_DEV="$(hdiutil attach -nomount "ram://$RAMDISK_SECTORS" | head -1 | awk '{print $1}')"
        sleep 1
        diskutil erasevolume HFS+ "$LABEL" "$RAMDISK_DEV" >/dev/null 2>&1

        # Take the mount point from the device rather than assuming it: if macOS renamed the volume
        # anyway, the rig must fill the disk it is actually using.
        VOLUME=""
        for _ in $(seq 1 20); do
            VOLUME="$(diskutil info "$RAMDISK_DEV" | awk -F: '/Mount Point/ {gsub(/^ +/, "", $2); print $2}')"
            if [ -n "$VOLUME" ] && [ -d "$VOLUME" ]; then break; fi
            sleep 0.5
        done

        if [ -z "$VOLUME" ] || [ ! -d "$VOLUME" ]; then
            echo "could not mount a RAM disk (device $RAMDISK_DEV)" >&2
            exit 1
        fi

        DB="$VOLUME/Homespool.Sqlite"
        echo "### database on $RAMDISK_DEV at $VOLUME ($(df -h "$VOLUME" | tail -1 | awk '{print $2}'))"
        ;;
    premounted)
        # $VOLUME is a small filesystem someone else mounted - a tmpfs, a loopback image. Same
        # mechanism as `full` from here on; only the setup and teardown differ.
        if [ ! -d "$VOLUME" ]; then
            echo "MECHANISM=premounted needs a filesystem mounted at $VOLUME" >&2
            exit 1
        fi

        DB="$VOLUME/Homespool.Sqlite"
        echo "### database on the pre-mounted volume at $VOLUME"
        ;;
    lock)
        DB="$RUN/Homespool.Sqlite"
        echo "### database on the ordinary disk (lock mechanism - expect blocking, not failure)"
        ;;
    *)
        echo "unknown MECHANISM '$MECHANISM' - use full, premounted or lock" >&2
        exit 1
        ;;
esac

cd "$ROOT/Homespool.Host"
# Ports come from Listeners:*, not ASPNETCORE_URLS - Kestrel ignores that entirely once endpoints are
# configured in code, which they are since the listener split.
ASPNETCORE_ENVIRONMENT=Development \
Listeners__UserPort="$PORT" \
Listeners__PrinterPort="$PRINTER_PORT" \
PrusaConnect__PrinterTls=false \
Serilog__MinimumLevel__Default=Information \
Storage__WriteBatchSize="$WRITE_BATCH_SIZE" \
ConnectionStrings__HomespoolDb="Data Source=$DB" \
    dotnet "$HOST_DLL" > "$RUN/server.log" 2>&1 &
SERVER_PID=$!

for _ in $(seq 1 60); do
    if curl -fsS -o /dev/null "$BASE/health/live" 2>/dev/null; then break; fi
    sleep 1
done

# First-run setup: the bootstrap token is logged as a CLEF property, not as a bare line.
TOKEN="$(grep -o '"SetupToken":"[^"]*"' "$RUN/server.log" | head -1 | sed 's/.*:"//; s/"$//')"
AF="$(curl -fsS -c "$RUN/cookies" "$BASE/setup" \
    | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' \
    | sed 's/.*value="//; s/"$//' | head -1)"
curl -s -o /dev/null -b "$RUN/cookies" -c "$RUN/cookies" \
    --data-urlencode "__RequestVerificationToken=$AF" \
    --data-urlencode "Input.Email=admin@example.com" \
    --data-urlencode "Input.Password=Correct-Horse-Battery-Staple-1!" \
    --data-urlencode "Input.ConfirmPassword=Correct-Horse-Battery-Staple-1!" \
    --data-urlencode "Input.Token=$TOKEN" "$BASE/setup"

dotnet "$CLI" enrol --server "$PRINTER_BASE" --identity "$RUN/fakeprinter.json" > "$RUN/enrol.log" 2>&1 &
ENROL_PID=$!
CODE=""
for _ in $(seq 1 30); do
    # `|| true`: until the CLI prints the code grep exits 1, and pipefail would kill the script.
    CODE="$(grep -o 'Claim code: .*' "$RUN/enrol.log" 2>/dev/null | head -1 | sed 's/Claim code: //' | tr -d '\r' || true)"
    if [ -n "$CODE" ]; then break; fi
    sleep 1
done
curl -s -o /dev/null -b "$RUN/cookies" -H 'Content-Type: application/json' \
    -d "{\"code\":\"$CODE\",\"name\":\"Stall rig\",\"location\":\"loopback\"}" \
    "$BASE/api/v1/printers/register"
wait $ENROL_PID

# Load is deliberately moderate, not a blast: the ceilings are reached by how long the outage lasts,
# not by how hard the client pushes, and a bounded rate keeps a log-volume measurement legible.
dotnet "$CLI" run --server "$PRINTER_BASE" --identity "$RUN/fakeprinter.json" --printing \
    --interval-ms "$INTERVAL_MS" --events-every "$EVENTS_EVERY" > "$RUN/load.log" 2>&1 &
LOAD_PID=$!

sleep "$WARMUP_SECONDS"
WARMUP_ROWS="$(sqlite3 "$DB" "select count(*) from TelemetrySamples;" 2>/dev/null || echo 0)"
echo "### warmup: $WARMUP_ROWS samples in ${WARMUP_SECONDS}s (~$((WARMUP_ROWS / WARMUP_SECONDS))/s), flushing normally"

STALL_START="$(date +%s)"

if [ "$MECHANISM" = "lock" ]; then
    # Held past the shutdown when the outage has to survive it; cleanup kills the holder either way.
    LOCK_HOLD_SECONDS="$STALL_SECONDS"

    if [ "$FREE_BEFORE_SHUTDOWN" != "1" ]; then
        LOCK_HOLD_SECONDS=$((STALL_SECONDS + RECOVERY_SECONDS + 180))
    fi

    python3 - "$DB" "$LOCK_HOLD_SECONDS" > "$RUN/lock.log" 2>&1 <<'PY' &
import sqlite3, sys, time
db, seconds = sys.argv[1], float(sys.argv[2])
con = sqlite3.connect(db, isolation_level=None, timeout=5)
con.execute("PRAGMA busy_timeout=5000")
con.execute("BEGIN IMMEDIATE")
print(f"write lock held at {time.time():.3f}", flush=True)
time.sleep(seconds)
con.execute("ROLLBACK")
con.close()
print(f"write lock released at {time.time():.3f}", flush=True)
PY
    LOCK_PID=$!
else
    # dd runs until ENOSPC, which is the point. SQLite may keep writing for a few seconds into space
    # it had already allocated - so anchor any timing to the first flush failure in the log, never
    # to this moment. Measuring from here understated the sample/event ordering by 3.5x once.
    dd if=/dev/zero of="$VOLUME/filler" bs=1m >/dev/null 2>&1
    echo "### volume filled: $(df -h "$VOLUME" | tail -1 | awk '{print $4}') free"
fi

printf 'elapsed,pendingSamples,pendingEvents,droppedMessages,discardedEvents,consecutiveFailures,status\n' > "$RUN/health.csv"
ABORTED=""
RELEASED=""

while true; do
    ELAPSED=$(( $(date +%s) - STALL_START ))

    if [ "$ELAPSED" -ge "$STALL_SECONDS" ] && [ -z "$RELEASED" ]; then
        RELEASED="yes"

        if [ "$FREE_BEFORE_SHUTDOWN" != "1" ]; then
            echo "### outage HELD past the stall window - the shutdown below is measured against a database that is still refusing writes"
        elif [ "$MECHANISM" = "lock" ]; then
            kill "$LOCK_PID" 2>/dev/null
        else
            rm -f "$VOLUME/filler"
            echo "### volume freed at ${ELAPSED}s: $(df -h "$VOLUME" | tail -1 | awk '{print $4}') free"
        fi
    fi

    if [ "$ELAPSED" -gt $((STALL_SECONDS + RECOVERY_SECONDS)) ]; then break; fi

    if [ $(( $(wc -c < "$RUN/server.log") / 1048576 )) -gt "$LOG_LIMIT_MB" ]; then
        echo "### ABORT: server log passed ${LOG_LIMIT_MB} MB at ${ELAPSED}s"
        ABORTED="yes"
        [ "$MECHANISM" != "lock" ] && rm -f "$VOLUME/filler"
        break
    fi

    curl -s --max-time 2 "$BASE/health" > "$RUN/h.json" 2>/dev/null
    python3 - "$RUN/h.json" "$ELAPSED" >> "$RUN/health.csv" <<'PY' || true
import json, sys
try:
    with open(sys.argv[1]) as f:
        h = json.load(f)
    d = next(c for c in h["checks"] if c["name"] == "telemetry-persistence")["data"]
    print(f'{sys.argv[2]},{d["pendingSamples"]},{d["pendingEvents"]},{d["droppedMessages"]},'
          f'{d["discardedEvents"]},{d["consecutiveFailures"]},{h["status"]}')
except Exception:
    pass
PY
    sleep 0.25
done

kill -INT "$LOAD_PID" 2>/dev/null
for _ in $(seq 1 25); do
    if ! kill -0 "$LOAD_PID" 2>/dev/null; then break; fi
    sleep 0.2
done
kill -9 "$LOAD_PID" 2>/dev/null
# Reaped so bash does not print its own "Killed: 9" job notice, which reads like a rig failure.
wait "$LOAD_PID" 2>/dev/null
LOAD_PID=""

# Timed graceful shutdown: during an outage the drain must still process every queued item, one
# failed flush each, so this is where a stuck database shows up as a ShutdownTimeout overrun.
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
    if time.perf_counter() - start > 90:
        print("### STILL RUNNING after 90s")
        break
    time.sleep(0.001)
print(f"### SIGTERM -> exit in {(time.perf_counter() - start) * 1000:.0f} ms")
PY
SERVER_PID=""

echo "### aborted: ${ABORTED:-no}"
echo "### server log: $(( $(wc -c < "$RUN/server.log") / 1048576 )) MB over $(wc -l < "$RUN/server.log" | tr -d ' ') lines"
echo "### rows: samples=$(sqlite3 "$DB" 'select count(*) from TelemetrySamples;' 2>&1) events=$(sqlite3 "$DB" 'select count(*) from PrinterEvents;' 2>&1)"
echo "### integrity_check: $(sqlite3 "$DB" 'pragma integrity_check;' 2>&1 | head -3)"

echo "### log by level:"
grep -o '"@l":"[A-Za-z]*"' "$RUN/server.log" | sort | uniq -c | sort -rn

echo "### Error/Warning messages, counted, first and last sighting:"
python3 - "$RUN/server.log" <<'PY'
import collections, json, sys
counts, first, last = collections.Counter(), {}, {}
with open(sys.argv[1], errors="replace") as f:
    for line in f:
        try:
            e = json.loads(line)
        except Exception:
            continue
        if e.get("@l") in ("Error", "Fatal", "Warning"):
            key = (e["@l"], (e.get("@mt") or e.get("@m", ""))[:88])
            counts[key] += 1
            first.setdefault(key, e.get("@t"))
            last[key] = e.get("@t")
for (level, text), n in counts.most_common(12):
    print(f"  {n:>8}  {level:<7} {text}")
    print(f"            {first[(level, text)]} .. {last[(level, text)]}")
PY

echo "### the two trims, anchored to the first flush failure:"
python3 - "$RUN/server.log" <<'PY'
import json, sys
from datetime import datetime

def at(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))

onset, firsts = None, {}
with open(sys.argv[1], errors="replace") as f:
    for line in f:
        try:
            e = json.loads(line)
        except Exception:
            continue
        m = e.get("@m", "")
        if onset is None and "Telemetry flush failed" in m:
            onset = at(e["@t"])
        if "buffered telemetry sample" in m:
            firsts.setdefault("sample trim", at(e["@t"]))
        elif "buffered printer event" in m:
            firsts.setdefault("event trim", at(e["@t"]))

if onset is None:
    print("  no flush ever failed - the outage did not bite (see MECHANISM=lock in the header)")
elif not firsts:
    print("  flushes failed but neither ceiling was reached - a longer stall, or a lower WRITE_BATCH_SIZE")
else:
    for kind, t in firsts.items():
        print(f"  first {kind:<12} +{(t - onset).total_seconds():.1f}s after the first flush failure")
    if len(firsts) == 2:
        s, e_ = firsts["sample trim"], firsts["event trim"]
        print(f"  events survived {(e_ - onset).total_seconds() / (s - onset).total_seconds():.1f}x longer than samples")
PY

echo "### buffers:"
python3 - "$RUN/health.csv" <<'PY'
import csv, sys
rows = list(csv.DictReader(open(sys.argv[1])))
if not rows:
    print("  no health samples")
else:
    print(f"  pendingSamples peak {max(int(r['pendingSamples']) for r in rows)}")
    print(f"  pendingEvents  peak {max(int(r['pendingEvents']) for r in rows)}")
    print(f"  consecutiveFailures peak {max(int(r['consecutiveFailures']) for r in rows)}")
    print(f"  final: pendingSamples={rows[-1]['pendingSamples']} pendingEvents={rows[-1]['pendingEvents']} "
          f"discardedEvents={rows[-1]['discardedEvents']} dropped={rows[-1]['droppedMessages']}")
    print(f"  statuses seen: {sorted({r['status'] for r in rows})}")
PY

echo "### artefacts in $RUN (server.log, health.csv, load.log, enrol.log)"
