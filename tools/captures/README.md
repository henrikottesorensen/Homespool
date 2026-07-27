# Capture decoders

Turn raw Prusa Connect captures into queryable JSONL.

```bash
python3 tools/captures/decode_captures.py   # packet captures
python3 tools/captures/decode_mitm.py       # mitmproxy dumps
```

Both default to reading `private-captures/` and writing to `private-captures/decoded/`, which is
gitignored. Point them elsewhere with `--captures-dir` / `--out`.

> **These scripts are committed; their output must never be.** A Connect capture carries live
> credentials in clear — the `INFO` event alone has the printer's `fingerprint`, `sn` and
> `api_key`, and `api_key` is the PrusaLink password. Redact before promoting anything into a
> committed fixture.

## Why not just tshark

`tshark` is the right cross-check and worth reaching for, but it cannot do the whole job here:

- **It decodes WebSocket only on streams whose HTTP upgrade it saw.** A capture started against
  an already-connected printer yields nothing at all.
- **It cannot merge a stream across capture files.** Several captures of one long-lived
  connection, taken as overlapping slices, only become a complete picture once joined on the TCP
  four-tuple.

Where the comparison is apples to apples the two agree exactly, so use tshark to check this.

## What the decoding has to get right

- **The printer is the WebSocket client, so its frames are XOR-masked.** `strings` sees only the
  server's half, which makes a capture look far emptier than it is. The masking bit is also how
  these scripts tell the two directions apart — more reliable than a port number, and it still
  works on a stream captured mid-session.
- **Reassembly must be gap-aware.** Concatenating segments in sequence order across a dropped
  segment shifts every later frame header and turns the stream into garbage that reads like a
  parser bug. Retransmits and overlaps need the same care, or a short retransmit truncates the
  original.
- **Messages fragment, heavily.** A single file listing or a gcode preview arrives as hundreds of
  continuation frames of a few hundred bytes each.

## Output

`messages.jsonl`, one record per WebSocket message in time order:

```json
{"t": 1700000000.06, "iso": "2023-11-14T22:13:20+00:00", "stream": "10.0.0.5:51234",
 "peer": "MK4", "dir": "p2s", "op": "text", "json": {"event": "REJECTED", "...": "..."}}
```

`dir` is `p2s` (printer→server) or `s2p`. `peer` is taken from the `User-Agent-Printer` header on
the upgrade when the capture caught it, falling back to the address. Server command frames also
carry `frame` (the `J`/`G`/`F`/`D`/`T` letter), `cmd_id` and `command`.

Also written: `http.jsonl` (registration handshake and WebSocket upgrades), `streams.json`
(per-stream summary), and `mitm-*.jsonl` from the proxy dumps.

Typical query:

```bash
jq -c 'select(.json.event=="REJECTED") | {iso, peer, json}' private-captures/decoded/messages.jsonl
```
