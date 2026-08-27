# Slow-database rig

Drives a real server with a real fake printer while the **database refuses to accept writes**, to
exercise `TelemetryWriter`'s failure path.

```bash
dotnet build Homespool.slnx
./tools/slow-db/slow-db-rig.sh                                  # 90 s outage
STALL_SECONDS=180 WRITE_BATCH_SIZE=50 ./tools/slow-db/slow-db-rig.sh
```

macOS for the default mechanism (it wants `hdiutil`/`diskutil` for a RAM disk). Everything it
creates is torn down on exit, Ctrl-C included; artefacts land in `$TMPDIR/homespool-slow-db-rig`.

## Why this exists rather than just blasting harder

`Homespool.FakePrinter.Cli blast` saturates the intake channel, and the channel sheds with
`DropOldest`. That is one stage *upstream* of the pending buffers, so no amount of client speed ever
grows them: measured on 2026-07-29, a 20 s blast pushed 2.85 M messages and left `pendingSamples`
and `pendingEvents` at **0**.

The ceilings in `TelemetryWriter` (`TrimExcessPendingSamples`, `TrimExcessPendingEvents`) exist for
the opposite failure — flushes that keep *failing*, where `SafeFlushAsync` deliberately keeps the
buffers for a retry and nothing else would stop them growing. Reaching that code needs a database
that rejects writes, not a fast printer. Hence a second rig.

## The two mechanisms, and why the obvious one doesn't work

- **`MECHANISM=full`** (default) — the database sits on a small RAM disk which is then filled, so
  every flush fails immediately with `SQLITE_FULL`. Repeated, instant rejection is exactly the shape
  the ceilings are for, and a volume filling up is a realistic outage.
- **`MECHANISM=lock`** — an outside connection holds `BEGIN IMMEDIATE`. **This does not fill the
  buffers, and that is the point of keeping it.** Microsoft.Data.Sqlite retries `SQLITE_BUSY`
  internally up to its command timeout (30 s by default), so a lock makes flushes *block* rather
  than fail: the buffers stay empty, the channel sheds instead, and the health endpoint's pending
  counts **freeze** at their last published value, because `PublishHealth` only runs in
  `SafeFlushAsync`'s `finally`.
- **`MECHANISM=premounted`** — same as `full`, but against a filesystem you mounted yourself (a
  small tmpfs, a loopback image). The Linux path; untested.

## Reading the output

Anchor every timing to **the first flush failure**, which is what the script reports, not to the
moment the disk fills. SQLite keeps writing into already-allocated space for several seconds after
`dd` finishes; measuring from the fill once understated the sample-versus-event ordering by 3.5×.

`WRITE_BATCH_SIZE` scales both ceilings together (`× 20` for samples, `× 10` for events), so
lowering it brings them within reach of a short run while leaving the 2:1 cap ratio and the 10:1
stream ratio — the two things the ordering claim rests on — untouched. 50 gives caps of 1,000 and
500.

The load is deliberately moderate (~750 msg/s), because the ceilings are reached by how long the
outage lasts rather than by how hard the client pushes, and a bounded rate keeps the log-volume
measurement legible. `--events-every` on the CLI is what makes the event stream exist at all.

## What it found on its first run (2026-07-29)

A single transient flush failure **wedged the writer permanently** — not until the database
recovered, but until the process restarted, with the shutdown drain failing too. EF's relationship
fix-up had written the flush's `Printer` stub onto the buffered rows' navigation properties, and
those rows survive a failed flush by design, so every later flush collided with its own fresh stub
and threw before reaching the database. Fixed by removing the navigations outright; the regression
is pinned by `AFlushFailureDoesNotWedgeTheWriterOnceTheDatabaseRecovers`.

Also measured: both ceilings hold exactly, events survive ~7.6× longer than samples, and an outage
turns three log sites into wire-rate ones.
