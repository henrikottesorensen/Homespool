#!/usr/bin/env python3
"""Drives the real Prusa Connect SDK against a running Homespool.

WHY THIS EXISTS. `notes/http-transport.md` records that the HTTP transport's ingest half is built
and that "the SDK-driven MK3S+-via-a-Pi case remains untested and ungated". This is what tests it.
The other rig in this directory runs the *firmware's* Connect client; this runs the *Python* one,
which is the client an MK3S+ behind a Raspberry Pi actually uses, and which speaks HTTP only.

WHAT IT PROVES, AND WHAT IT DOES NOT. Everything on the wire comes from the SDK itself - its Printer
class, its register(), its LoopObjects, its make_headers(), and optionally its loop(). So a pass here
is a fact about the SDK's behaviour rather than about this script. What it cannot prove is anything
PrusaLink adds on top: it does not parse gcode, drive a serial link, or serve PrusaLink's own API.

    ./rig/sdk-rig.py http://localhost:15443 --register-only     # prints CLAIM_CODE
    ./rig/sdk-rig.py http://localhost:15443 --poll <code>       # prints TOKEN once claimed
    ./rig/sdk-rig.py http://localhost:15443 --token <token>     # one telemetry and one event
    ./rig/sdk-rig.py http://localhost:15443 --token <token> --loop

Claiming the code is a person's job. `rig/sdk-claim.py` does that part when nobody is watching.

The SDK is not vendored - install it where you run this:

    python3 -m venv /tmp/sdkenv
    /tmp/sdkenv/bin/pip install git+https://github.com/prusa3d/Prusa-Connect-SDK-Printer.git

Point it at the PRINTER listener (15443), not the people-facing one. With PrusaConnect__PrinterTls
false that listener is plain HTTP, which is what makes this runnable without teaching the SDK to
trust a private CA - see .env.example on PRINTER_TLS, and note it is a testing setting.
"""
import sys
import threading
import time

from prusa.connect.printer import Printer, const, errors
from prusa.connect.printer.const import Event as EventType, Source, State
from prusa.connect.printer.models import Event, Register, Telemetry

# Any 37-character string. The SDK sends the same value in the body and in every header, where the
# firmware truncates to 16 in headers only - the trap notes/cross-channel-identity-bug.md is about.
# Homespool keys on the first 16 characters of whichever form arrives, so a consistent sender agrees
# with itself and the trap does not apply here. Long on purpose, so that truncation is exercised.
FINGERPRINT = "sdkshimfingerprint0123456789abcdef0123"
SERIAL = "SDKSHIM0001"
FIRMWARE = "3.13.3"


def build(server, fingerprint, token=None):
    printer = Printer(const.PrinterType.I3MK3, SERIAL, fingerprint)
    printer.server = server
    printer.firmware = FIRMWARE

    if token:
        printer.token = token

    return printer


def report(name, ok, detail=""):
    print(f"  [{'OK ' if ok else 'FAIL'}] {name}{': ' + detail if detail else ''}")
    return ok


def register(printer):
    print("\n== register ==")
    code = printer.register()
    report("POST /p/register", bool(code), f"Code={code}")
    print(f"CLAIM_CODE={code}")
    return 0


def poll(printer, code, seconds=60):
    """Waits for the token.

    The SDK has no get_token(): the token arrives by looping a Register object until a response
    carries a Token header, so that is what this does - with the SDK's Register, not a hand-built
    request. Homespool answers 202 while the code is unclaimed, which is the poll-again signal.
    """
    print("\n== poll for token ==")
    deadline = time.time() + seconds
    last = None

    while time.time() < deadline:
        res = Register(code).send(printer.conn, printer.server, printer.make_headers())
        last = res.status_code

        if res.status_code == 200 and "Token" in res.headers:
            token = res.headers["Token"]
            report("GET /p/register", True, f"HTTP 200, Token ({len(token)} chars)")
            print(f"TOKEN={token}")
            return 0

        time.sleep(2)

    return 1 if not report("GET /p/register", False,
                           f"no Token in {seconds}s (last HTTP {last}) - was the code claimed?") else 0


def one_shot(printer):
    print("\n== telemetry ==")
    res = Telemetry(State.IDLE, temp_nozzle=24.5, temp_bed=23.0, material="PLA").send(
        printer.conn, printer.server, printer.make_headers())

    # 204 is the ordinary answer and means "stored, nothing for you". A 200 would carry a command,
    # which Homespool cannot send over this transport yet - see the note.
    report("POST /p/telemetry", res.status_code in (200, 204),
           f"HTTP {res.status_code}"
           + (f" Command-Id={res.headers['Command-Id']}" if "Command-Id" in res.headers else ""))

    print("\n== event ==")
    res = Event(EventType.INFO, Source.FIRMWARE, state=State.IDLE, firmware=FIRMWARE,
                sn=SERIAL, fingerprint=printer.fingerprint).send(
        printer.conn, printer.server, printer.make_headers())

    report("POST /p/events", res.status_code in (200, 204), f"HTTP {res.status_code}")
    return 0


def run_loop(printer, seconds=25):
    """Runs the SDK's own loop, which is what PrusaLink does rather than firing one-shot requests.

    The flags printed at the end are the SDK's own health model. They are the closest thing it has
    to an opinion about whether the server it is talking to is behaving.
    """
    threading.Thread(target=printer.loop, daemon=True).start()

    for _ in range(max(1, seconds // 5)):
        printer.telemetry(State.IDLE, temp_nozzle=25.0, temp_bed=24.0)
        time.sleep(5)

    print("\n== the SDK's own condition flags after the loop ran ==")
    ok = True

    for name in ("API", "HTTP", "TOKEN", "INTERNET"):
        condition = getattr(errors, name, None)

        if condition is not None:
            ok &= bool(getattr(condition, "ok", False))
            report(name, bool(getattr(condition, "ok", False)))

    return 0 if ok else 1


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    server = sys.argv[1].rstrip("/")
    args = sys.argv[2:]

    fingerprint = args[args.index("--fingerprint") + 1] if "--fingerprint" in args else FINGERPRINT
    token = args[args.index("--token") + 1] if "--token" in args else None

    printer = build(server, fingerprint, token)
    print(f"server={server}\nfingerprint={fingerprint}")

    if "--register-only" in args:
        return register(printer)

    if "--poll" in args:
        return poll(printer, args[args.index("--poll") + 1])

    if token is None:
        print("give --token, or --register-only then --poll")
        return 2

    return run_loop(printer) if "--loop" in args else one_shot(printer)


if __name__ == "__main__":
    sys.exit(main())
