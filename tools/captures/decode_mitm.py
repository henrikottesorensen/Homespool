#!/usr/bin/env python3
"""Decode mitmproxy captures of a Connect session into the same JSONL shape as
`decode_captures.py`, so pcap-derived and proxy-derived messages can be queried together.

Handles both of mitmproxy's outputs:
  * `.flows` / `.mitm` — its own tnetstring-encoded flow dump
  * `.jsonl`           — a JSON-Lines addon's output

Usage:
    python3 decode_mitm.py [--captures-dir DIR] [--out DIR] [dump ...]

With no file arguments it decodes every `*.flows`/`*.mitm` in the captures directory.  JSON-Lines
dumps are skipped by default: they are usually a second recording of a session the flow dump
already holds, and the flow dump is the better source — full headers and a real direction flag.
Pass one explicitly to decode it; identical messages are de-duplicated across sources.

Outputs `mitm-messages.jsonl` and `mitm-http.jsonl`, alongside the pcap decoder's output.

Same warning as the pcap decoder: a Connect capture carries live `fingerprint` / `sn` /
`api_key` values in clear, so this script is committed but its output must not be.
"""

import argparse
import datetime
import glob
import json
import os
import sys


# ----------------------------------------------------------------- tnetstrings

def tns_parse(data, pos=0):
    colon = data.index(b":", pos)
    length = int(data[pos:colon])
    payload = data[colon + 1 : colon + 1 + length]
    kind = data[colon + 1 + length : colon + 2 + length]
    nxt = colon + 2 + length
    if kind == b",":
        return payload, nxt
    if kind == b";":
        return payload.decode("utf-8", "replace"), nxt
    if kind == b"#":
        return int(payload), nxt
    if kind == b"^":
        return float(payload), nxt
    if kind == b"!":
        return payload == b"true", nxt
    if kind == b"~":
        return None, nxt
    if kind == b"]":
        out, p = [], 0
        while p < len(payload):
            value, p = tns_parse(payload, p)
            out.append(value)
        return out, nxt
    if kind == b"}":
        out, p = {}, 0
        while p < len(payload):
            key, p = tns_parse(payload, p)
            value, p = tns_parse(payload, p)
            out[key if isinstance(key, str) else key.decode()] = value
        return out, nxt
    raise ValueError(f"unknown tnetstring type {kind!r} at offset {colon}")


def load_flows(path):
    data = open(path, "rb").read()
    flows, pos = [], 0
    while pos < len(data):
        flow, pos = tns_parse(data, pos)
        flows.append(flow)
    return flows


# --------------------------------------------------------------------- helpers

def as_text(value):
    if isinstance(value, bytes):
        return value.decode("utf-8", "replace")
    return value if value is None else str(value)


def iso(ts):
    return datetime.datetime.fromtimestamp(ts, datetime.UTC).isoformat()


def classify(text):
    """Match decode_captures.decode_payload's output for a text message."""
    out = {}
    if len(text) > 9 and text[0] in "JGFDT" and all(c in "0123456789abcdefABCDEF" for c in text[1:9]):
        out["frame"] = text[0]
        out["cmd_id"] = int(text[1:9], 16)
        body = text[9:]
    else:
        body = text
    try:
        parsed = json.loads(body)
        out["json"] = parsed
        if isinstance(parsed, dict) and "command" in parsed:
            out["command"] = parsed["command"]
    except ValueError:
        out["text"] = body
    return out


def headers_of(obj):
    return {as_text(k): as_text(v) for k, v in (obj.get("headers") or [])}


def dedupe(records, key):
    """Drop records two sources recorded identically."""
    seen, out, dropped = set(), [], 0
    for record in records:
        k = key(record)
        if k in seen:
            dropped += 1
            continue
        seen.add(k)
        out.append(record)
    if dropped:
        print(f"  dropped {dropped} duplicate records (same message recorded by two sources)")
    return out


def peer_of(messages):
    """Name the printer from its INFO event, which carries the model as `printer_type`."""
    for entry in messages:
        data = (entry.get("json") or {}).get("data") or {}
        if isinstance(data, dict) and data.get("printer_type"):
            return str(data["printer_type"])
    return "printer"


# ------------------------------------------------------------------------ main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dumps", nargs="*", help="mitmproxy dumps; default is every *.flows/*.mitm in --captures-dir")
    ap.add_argument("--captures-dir", default="private-captures", dest="captures_dir")
    ap.add_argument("--out", default=None, help="output directory (default: <captures-dir>/decoded)")
    args = ap.parse_args()

    sources = args.dumps or sorted(
        glob.glob(os.path.join(args.captures_dir, "*.flows")) + glob.glob(os.path.join(args.captures_dir, "*.mitm"))
    )
    if not sources:
        ap.error(f"no dumps given and none found in {args.captures_dir}")

    out = args.out or os.path.join(args.captures_dir, "decoded")
    os.makedirs(out, exist_ok=True)

    messages, https = [], []

    for source in sources:
        path = source if os.path.exists(source) else os.path.join(args.captures_dir, source)
        name = os.path.basename(path)
        batch, batch_http = [], []

        if path.endswith(".jsonl"):
            for line in open(path):
                record = json.loads(line)
                if record.get("kind") == "ws":
                    entry = {
                        "t": record.get("t"),
                        "source": name,
                        "dir": "p2s" if record.get("from") == "printer" else "s2p",
                        "op": "text",
                    }
                    entry.update(classify((record.get("body") or {}).get("text", "")))
                    batch.append(entry)
                else:
                    batch_http.append(
                        {
                            "t": record.get("t"),
                            "source": name,
                            "dir": "p2s" if record.get("kind") == "request" else "s2p",
                            "start_line": f"{record.get('method', '')} {record.get('url', '')} "
                            f"{record.get('status_code', '')}".strip(),
                            "headers": record.get("headers", {}),
                            "body": (record.get("body") or {}).get("text", ""),
                        }
                    )
        else:
            for flow in load_flows(path):
                request = flow.get("request") or {}
                response = flow.get("response") or {}
                start = request.get("timestamp_start") or flow.get("timestamp_created")
                url = f"{as_text(request.get('scheme'))}://{as_text(request.get('host'))}{as_text(request.get('path'))}"
                batch_http.append(
                    {
                        "t": start,
                        "source": name,
                        "dir": "p2s",
                        "start_line": f"{as_text(request.get('method'))} {url}",
                        "headers": headers_of(request),
                        "body": as_text(request.get("content")) or "",
                    }
                )
                if response:
                    end = response.get("timestamp_start") or start
                    batch_http.append(
                        {
                            "t": end,
                            "source": name,
                            "dir": "s2p",
                            "start_line": f"HTTP {response.get('status_code')} "
                            f"{as_text(response.get('reason'))} ({url})",
                            "headers": headers_of(response),
                            "body": as_text(response.get("content")) or "",
                        }
                    )

                # mitmproxy stores each message as [type, from_client, content, timestamp, ...]
                for message in (flow.get("websocket") or {}).get("messages") or []:
                    ts = message[3] if len(message) > 3 else None
                    entry = {
                        "t": ts,
                        "source": name,
                        "dir": "p2s" if message[1] else "s2p",
                        "op": "text",
                    }
                    entry.update(classify(as_text(message[2])))
                    batch.append(entry)

        peer = peer_of(batch)
        for entry in batch + batch_http:
            entry["peer"] = peer
            entry["iso"] = iso(entry["t"]) if entry.get("t") else None
        messages += batch
        https += batch_http

    messages = dedupe(
        messages,
        lambda r: (round(r["t"] or 0, 3), r["dir"], json.dumps(r.get("json"), sort_keys=True), r.get("text")),
    )
    https = dedupe(https, lambda r: (round(r["t"] or 0, 3), r["dir"], r["body"]))

    messages.sort(key=lambda r: r["t"] or 0)
    https.sort(key=lambda r: r["t"] or 0)

    with open(os.path.join(out, "mitm-messages.jsonl"), "w") as fh:
        for record in messages:
            fh.write(json.dumps(record) + "\n")
    with open(os.path.join(out, "mitm-http.jsonl"), "w") as fh:
        for record in https:
            fh.write(json.dumps(record) + "\n")

    print(f"{len(messages)} websocket messages, {len(https)} http blocks -> {out}")


if __name__ == "__main__":
    sys.exit(main())
