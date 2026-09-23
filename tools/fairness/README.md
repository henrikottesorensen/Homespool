# Fairness rig

Asks whether **one printer can starve another**. Two fake printers connect to one real server: a
victim sending at a printer's pace, and an attacker running `blast`. The rig reports what reached the
database from each of them before, during and after the blast.

```bash
dotnet build Homespool.slnx
./tools/fairness/fairness-rig.sh
BLAST_SECONDS=60 ./tools/fairness/fairness-rig.sh
AFTER_SECONDS=420 ./tools/fairness/fairness-rig.sh
```

It needs `curl`, `sqlite3` and `python3`. It kills everything it started on exit, Ctrl-C included,
and leaves its artefacts in `$TMPDIR/homespool-fairness-rig`. It runs on the same ports as the
slow-database rig (5099 and 15443), so run one at a time or set `PORT` and `PRINTER_PORT`.

## Why two printers

Every printer's telemetry and events share one bounded channel into `TelemetryWriter`, and the
channel drops its oldest item when full. A blast from one socket therefore says nothing about the
question that matters, which is whose messages get dropped. It takes a second printer, sending at a
normal pace, to see that.

## Reading the output

- **The quiet window is the yardstick.** The victim's settings (`VICTIM_INTERVAL_MS`,
  `VICTIM_EVENT_SECONDS`) are not what it lands: an event takes a telemetry tick's place, so the
  defaults land half a sample and half an event a second. The blast and after windows are read
  against what the quiet window measured, and the rig prints the victim's share of each.
- **Rows are binned by their stored `Timestamp`**, which is when the server received the message,
  not when it was written. A flush that lags does not move a row into the wrong window.
- **The attacker's rows during the blast are its throughput.** Stored samples are one per telemetry
  message while `Storage:MinimumSampleIntervalSeconds` is 0, as it is by default.
- **Rows from the attacker in the after window are its backlog.** The bytes it had already sent are
  still read after the process dies, at whatever rate the socket's message budget allows.

## What it found (2026-09-23)

A 20-second blast, on a Debug build on a Mac:

| victim, per second | quiet | blast | after |
|---|---|---|---|
| before the per-socket budget existed: samples / events | 0.50 / 0.50 | **0.00 / 0.00** | 0.44 / 0.34 |
| the budget lifted to a million a second: samples / events | 0.50 / 0.50 | **0.10 / 0.00** | 0.39 / 0.34 |
| 5 a second after a burst of 60, the default: samples / events | 0.50 / 0.50 | **0.50 / 0.50** | 0.54 / 0.49 |

Without a budget the writer dropped two to four million messages and the victim lost all its events
and most or all of its samples for the whole blast. With the default budget nothing was dropped, and
the attacker landed 158 samples in 20 seconds: its burst of 60 and then 5 a second.

**Ten thousand a second is not "no budget"** on this machine: the attacker was held to about that,
the writer dropped under a hundred thousand, and the victim lost nothing. Where between there and the
default a budget stops protecting other printers depends on how fast the writer drains, which on a
Raspberry Pi has not been measured.

The budget has a cost it does not hide: **a dead sender's backlog is drained at the budget's pace.**
After the attacker was killed, its bytes already in flight were still being read at 5 a second when
the rig stopped the server, 425 seconds later. The victim was unaffected throughout.

To compare against no budget at all, lift it out of reach (the settings allow up to a million):

```bash
EXTRA_ENV="PrusaConnect__MessagesPerSecond=1000000 PrusaConnect__MessageBurst=1000000" \
    ./tools/fairness/fairness-rig.sh
```
