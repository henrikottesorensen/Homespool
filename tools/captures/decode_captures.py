#!/usr/bin/env python3
"""Decode Prusa Connect packet captures into timestamped JSONL.

A printer speaks the Connect protocol over a WebSocket, as the *client* — so its frames are
XOR-masked and invisible to `strings`, which shows only the server's half and makes a capture
look far emptier than it is.  This walks the pcap, reassembles each TCP stream, unmasks the
frames, de-fragments continuations, and writes one JSON object per protocol message.

Usage:
    python3 decode_captures.py [--captures-dir DIR] [--out DIR] [capture.pcap ...]

With no file arguments it decodes every `*.cap`/`*.pcap` in the captures directory.  Streams
sharing a TCP four-tuple are merged across files, since captures of one long-lived connection
are often taken as several overlapping slices.

Outputs, in --out:
    messages.jsonl   every WebSocket message, both directions, in time order
    http.jsonl       every HTTP request/response (registration, the WebSocket upgrade)
    streams.json     per-TCP-stream summary

Record shape in messages.jsonl:
    {"t": 1700000000.06, "iso": "...", "stream": "10.0.0.5:51234", "peer": "MK4",
     "dir": "p2s" | "s2p", "op": "text" | "ping" | "pong" | "close",
     "frame": "J", "cmd_id": 320, "command": "SEND_INFO",   # server command frames only
     "json": { ... }}                                       # parsed payload where it is JSON

Direction is derived from the WebSocket masking bit rather than from port numbers, so it is
correct even for a stream captured mid-session with no handshake in view.

NOTE: a Connect capture carries live credentials in clear — the INFO event alone has the
printer's `fingerprint`, `sn` and `api_key`, and `api_key` is the PrusaLink password.  This
script is committed; its output must not be.  Redact before promoting anything into a fixture.
"""

import argparse
import datetime
import glob
import json
import os
import re
import socket
import struct
import sys
from collections import defaultdict

OPCODES = {0: "cont", 1: "text", 2: "binary", 8: "close", 9: "ping", 10: "pong"}


# --------------------------------------------------------------------------- pcap

def read_pcap(path):
    """Yield (timestamp, link_type, packet_bytes)."""
    data = open(path, "rb").read()
    magic = data[:4]
    if magic == b"\xd4\xc3\xb2\xa1":
        endian = "<"
    elif magic == b"\xa1\xb2\xc3\xd4":
        endian = ">"
    else:
        raise ValueError(f"{path}: not a classic pcap ({magic!r})")
    link = struct.unpack(endian + "I", data[20:24])[0]
    pos = 24
    while pos + 16 <= len(data):
        ts, tus, incl, _orig = struct.unpack(endian + "IIII", data[pos : pos + 16])
        pos += 16
        yield ts + tus / 1e6, link, data[pos : pos + incl]
        pos += incl


def parse_packet(link, pkt):
    """Return (src, sport, dst, dport, seq, flags, payload) for IPv4/TCP, else None."""
    if link == 1:  # Ethernet
        if len(pkt) < 14:
            return None
        ethertype = struct.unpack("!H", pkt[12:14])[0]
        off = 14
        while ethertype in (0x8100, 0x88A8):  # VLAN tags
            ethertype = struct.unpack("!H", pkt[off + 2 : off + 4])[0]
            off += 4
        if ethertype != 0x0800:
            return None
        ip = pkt[off:]
    elif link == 101:  # RAW
        ip = pkt
    elif link == 113:  # LINUX_SLL
        if struct.unpack("!H", pkt[14:16])[0] != 0x0800:
            return None
        ip = pkt[16:]
    elif link == 0:  # NULL / loopback
        ip = pkt[4:]
    else:
        return None

    if len(ip) < 20 or (ip[0] >> 4) != 4 or ip[9] != 6:
        return None
    ihl = (ip[0] & 0xF) * 4
    total = struct.unpack("!H", ip[2:4])[0]
    src = socket.inet_ntoa(ip[12:16])
    dst = socket.inet_ntoa(ip[16:20])
    tcp = ip[ihl:total] if total else ip[ihl:]
    if len(tcp) < 20:
        return None
    sport, dport = struct.unpack("!HH", tcp[0:4])
    seq = struct.unpack("!I", tcp[4:8])[0]
    data_off = (tcp[12] >> 4) * 4
    return src, sport, dst, dport, seq, tcp[13], tcp[data_off:]


class Stream:
    """TCP payload reassembly keyed on sequence number.

    Keeps the *longest* segment seen for a sequence number, so a short retransmit never
    truncates the original, and yields contiguous runs rather than blindly concatenating:
    joining across a dropped segment shifts every later WebSocket frame header and turns the
    whole stream into garbage that reads like a parser bug.
    """

    def __init__(self):
        self.segments = {}  # seq -> payload
        self.times = {}  # seq -> capture timestamp
        self.captures = set()

    def add(self, seq, payload, ts, capture):
        previous = self.segments.get(seq)
        if previous is None or len(payload) > len(previous):
            self.segments[seq] = payload
            self.times[seq] = ts
        self.captures.add(capture)

    def runs(self):
        """Yield (bytes, [(offset_in_run, timestamp), ...]) for each contiguous run."""
        if not self.segments:
            return
        buf = bytearray()
        marks = []
        expected = None
        for seq in sorted(self.segments):
            segment = self.segments[seq]
            if expected is None or seq == expected:
                pass
            elif seq < expected:  # overlap
                if seq + len(segment) <= expected:
                    continue
                segment = segment[expected - seq :]
            else:  # gap: end the run here
                yield bytes(buf), marks
                buf, marks, expected = bytearray(), [], None
            marks.append((len(buf), self.times[seq]))
            buf += segment
            expected = seq + len(self.segments[seq])
        if buf:
            yield bytes(buf), marks


def time_at(marks, offset):
    """Timestamp of the packet that carried byte `offset` of a run."""
    best = marks[0][1]
    for start, ts in marks:
        if start > offset:
            break
        best = ts
    return best


# --------------------------------------------------------------------- http / ws

def split_http(data):
    """Return (list of (headers, body), remainder after a 101 upgrade)."""
    blocks = []
    pos = 0
    while True:
        end = data.find(b"\r\n\r\n", pos)
        if end < 0:
            break
        head = data[pos:end].decode("latin1")
        if not re.match(r"^(HTTP/1\.[01] \d{3}|[A-Z]+ \S+ HTTP/1\.[01])", head):
            break
        body_start = end + 4
        lowered = head.lower()
        if "101 switching protocols" in lowered:
            blocks.append((head, b""))
            return blocks, data[body_start:]
        length = re.search(r"content-length:\s*(\d+)", lowered)
        if length:
            n = int(length.group(1))
            blocks.append((head, data[body_start : body_start + n]))
            pos = body_start + n
        elif "transfer-encoding: chunked" in lowered:
            p, body = body_start, b""
            while True:
                nl = data.find(b"\r\n", p)
                if nl < 0:
                    return blocks, b""
                try:
                    size = int(data[p:nl].split(b";")[0], 16)
                except ValueError:
                    return blocks, b""
                if size == 0:
                    tail = data.find(b"\r\n\r\n", nl)
                    p = tail + 4 if tail >= 0 else len(data)
                    break
                body += data[nl + 2 : nl + 2 + size]
                p = nl + 2 + size + 2
            blocks.append((head, body))
            pos = p
        else:
            blocks.append((head, b""))
            pos = body_start
    return blocks, data[pos:]


def ws_frames(data, base_offset=0):
    """Yield (offset, fin, opcode, masked, payload).  Stops at the first malformed frame."""
    pos = 0
    while pos + 2 <= len(data):
        b0, b1 = data[pos], data[pos + 1]
        opcode = b0 & 0xF
        if (b0 & 0x70) or opcode not in OPCODES:  # RSV bits set, or unknown opcode
            return
        fin = b0 >> 7
        masked = b1 >> 7
        length = b1 & 0x7F
        cursor = pos + 2
        if length == 126:
            if cursor + 2 > len(data):
                return
            length = struct.unpack("!H", data[cursor : cursor + 2])[0]
            cursor += 2
        elif length == 127:
            if cursor + 8 > len(data):
                return
            length = struct.unpack("!Q", data[cursor : cursor + 8])[0]
            cursor += 8
        key = b""
        if masked:
            if cursor + 4 > len(data):
                return
            key = data[cursor : cursor + 4]
            cursor += 4
        if cursor + length > len(data):
            return
        payload = bytearray(data[cursor : cursor + length])
        if masked:
            for i in range(length):
                payload[i] ^= key[i & 3]
        yield base_offset + pos, fin, opcode, bool(masked), bytes(payload)
        pos = cursor + length


def defragment(frames):
    """Merge continuation frames into whole messages.

    Firmware chunks a large event into hundreds of small continuation frames, so this is not
    an edge case — a single file listing can arrive as a few hundred of them.
    """
    offset = None
    buf = None
    opcode = None
    for off, fin, op, masked, payload in frames:
        if op in (8, 9, 10):  # control frames are never fragmented
            yield off, op, masked, payload
            continue
        if op == 0:
            if buf is None:
                continue
            buf += payload
        else:
            offset, buf, opcode = off, bytearray(payload), op
        if fin and buf is not None:
            yield offset, opcode, masked, bytes(buf)
            buf = None


# A server command frame: one letter naming the frame type, eight hex digits of command id,
# then the payload.
COMMAND_FRAME = re.compile(rb"^([JGFDT])([0-9a-fA-F]{8})(.*)$", re.S)


def decode_payload(opcode, payload, masked):
    """Classify a message payload.  Returns a dict of extra record fields."""
    out = {}
    if opcode != 1:
        if opcode == 8 and len(payload) >= 2:
            out["close_code"] = struct.unpack("!H", payload[:2])[0]
            out["close_reason"] = payload[2:].decode("utf-8", "replace")
        return out
    text = payload.decode("utf-8", "replace")
    match = None if masked else COMMAND_FRAME.match(payload)
    if match:  # only the server sends command frames, and only the server sends unmasked
        out["frame"] = match.group(1).decode()
        out["cmd_id"] = int(match.group(2), 16)
        body = match.group(3)
        try:
            parsed = json.loads(body)
            out["json"] = parsed
            if isinstance(parsed, dict) and "command" in parsed:
                out["command"] = parsed["command"]
        except ValueError:
            out["text"] = body.decode("utf-8", "replace")
        return out
    try:
        out["json"] = json.loads(text)
    except ValueError:
        out["text"] = text
    return out


def header_value(head, name):
    for line in head.split("\r\n")[1:]:
        key, _, value = line.partition(":")
        if key.strip().lower() == name:
            return value.strip()
    return None


# ------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("captures", nargs="*", help="capture files; default is every *.cap/*.pcap in --captures-dir")
    ap.add_argument("--captures-dir", default="private-captures", dest="captures_dir")
    ap.add_argument("--out", default=None, help="output directory (default: <captures-dir>/decoded)")
    args = ap.parse_args()

    files = args.captures or sorted(
        glob.glob(os.path.join(args.captures_dir, "*.cap")) + glob.glob(os.path.join(args.captures_dir, "*.pcap"))
    )
    if not files:
        ap.error(f"no captures given and none found in {args.captures_dir}")

    out = args.out or os.path.join(args.captures_dir, "decoded")
    os.makedirs(out, exist_ok=True)

    streams = defaultdict(Stream)
    for name in files:
        path = name if os.path.exists(name) else os.path.join(args.captures_dir, name)
        for ts, link, pkt in read_pcap(path):
            parsed = parse_packet(link, pkt)
            if not parsed:
                continue
            src, sport, dst, dport, seq, _flags, payload = parsed
            if payload:
                streams[(src, sport, dst, dport)].add(seq, payload, ts, os.path.basename(path))

    # Pass one: work out which end of each stream is the client, and what to call it.  A
    # WebSocket client must mask and a server must not, so the masking bit settles direction
    # without knowing which port the server listened on.  The upgrade request, when the
    # capture caught it, names the printer far more usefully than an address does.
    decoded = {}
    labels = {}
    for key, stream in streams.items():
        runs = list(stream.runs())
        blocks, messages, from_client = [], [], None
        for data, marks in runs:
            http, rest = split_http(data)
            blocks += [(marks[0][1], head, body) for head, body in http]
            for head, _ in http:
                if not head.startswith("HTTP/"):
                    from_client = True
                    model = header_value(head, "user-agent-printer")
                    if model:
                        labels[key] = model
                else:
                    from_client = False
            offset = len(data) - len(rest)
            for off, opcode, masked, payload in defragment(ws_frames(rest, offset)):
                if opcode != 0:
                    from_client = masked
                messages.append((time_at(marks, off), opcode, masked, payload))
        decoded[key] = (runs, blocks, messages, bool(from_client))

    # A printer's two stream directions share an address pair, so a name learned from one
    # applies to the other.
    for (src, sport, dst, dport) in list(decoded):
        reverse = (dst, dport, src, sport)
        if reverse in labels and (src, sport, dst, dport) not in labels:
            labels[(src, sport, dst, dport)] = labels[reverse]

    records, https, summary = [], [], []
    for key, (runs, blocks, messages, from_client) in decoded.items():
        src, sport, dst, dport = key
        endpoint = f"{src}:{sport}" if from_client else f"{dst}:{dport}"
        peer = labels.get(key, endpoint)
        direction = "p2s" if from_client else "s2p"
        counts = defaultdict(int)

        for ts, head, body in blocks:
            lines = head.split("\r\n")
            https.append(
                {
                    "t": ts,
                    "iso": datetime.datetime.fromtimestamp(ts, datetime.UTC).isoformat(),
                    "stream": endpoint,
                    "peer": peer,
                    "dir": direction,
                    "start_line": lines[0],
                    "headers": dict(
                        (line.split(":", 1)[0].strip(), line.split(":", 1)[1].strip())
                        for line in lines[1:]
                        if ":" in line
                    ),
                    "body": body.decode("utf-8", "replace"),
                }
            )

        for ts, opcode, masked, payload in messages:
            counts[OPCODES.get(opcode, opcode)] += 1
            record = {
                "t": round(ts, 6),
                "iso": datetime.datetime.fromtimestamp(ts, datetime.UTC).isoformat(),
                "captures": sorted(streams[key].captures),
                "stream": endpoint,
                "peer": peer,
                "dir": "p2s" if masked else "s2p",
                "op": OPCODES.get(opcode, opcode),
            }
            record.update(decode_payload(opcode, payload, masked))
            records.append(record)

        summary.append(
            {
                "stream": f"{src}:{sport} -> {dst}:{dport}",
                "peer": peer,
                "dir": direction,
                "bytes": sum(len(d) for d, _ in runs),
                "runs": len(runs),
                "http": len(blocks),
                "frames": dict(counts),
                "captures": sorted(streams[key].captures),
            }
        )

    records.sort(key=lambda r: r["t"])
    https.sort(key=lambda r: r["t"])

    with open(os.path.join(out, "messages.jsonl"), "w") as fh:
        for record in records:
            fh.write(json.dumps(record) + "\n")
    with open(os.path.join(out, "http.jsonl"), "w") as fh:
        for record in https:
            fh.write(json.dumps(record) + "\n")
    with open(os.path.join(out, "streams.json"), "w") as fh:
        json.dump(sorted(summary, key=lambda s: -s["bytes"]), fh, indent=1)

    print(f"{len(records)} websocket messages, {len(https)} http blocks -> {out}")
    for entry in sorted(summary, key=lambda s: -s["bytes"]):
        print(f"  {entry['stream']:<45} {entry['peer']:<12} {entry['bytes']:>9}B {entry['frames']}")


if __name__ == "__main__":
    sys.exit(main())
