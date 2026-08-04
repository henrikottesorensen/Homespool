# Printer TLS: why nginx terminates it, and what a replacement must do

You do not need this document to run Homespool. The shipped `compose.yaml` already does everything
here. Read it if you are **replacing the proxy in front of the printer port**, debugging a transfer
that dies for no visible reason, or wondering why the application does not serve printers itself.

## The constraint

A Prusa printer takes about **a kilobyte of TLS plaintext per record**. That is its mbedtls input
buffer — Buddy firmware sets `MBEDTLS_SSL_IN_CONTENT_LEN` to 1024 and `MBEDTLS_SSL_OUT_CONTENT_LEN`
to 512, which reclaims roughly 30 KB of SRAM on a board that has 128–192 KB in total. It is a
deliberate trade by a team fitting TLS into a microcontroller, not an oversight.

**The printer never advertises this.** Its ClientHello offers `server_name`, `signature_algorithms`,
`supported_groups`, `ec_point_formats` and a single ciphersuite, and nothing else. There is no
RFC 6066 `max_fragment_length` extension, so there is nothing for a server to honour and no
negotiation to get right. The limit is real, unadvertised, and has to be **imposed** by whatever
terminates TLS.

Nor could that change easily: RFC 8449 `record_size_limit`, the modern replacement, only exists in
mbedTLS 3.x, and Buddy vendors 2.28.

## Why not just let the application do it

`SslStream` cannot bound record size. .NET exposes no API for it on any platform, and
[dotnet/runtime#44241](https://github.com/dotnet/runtime/issues/44241) — asking for exactly this from
the IoT side — was opened in 2020 and closed unimplemented. Hand `SslStream` an 8 KB write and it
emits a single 8216-byte record: sixteen times what an MK3.5 can hold.

So the proxy is **structural, not a deployment convenience**. The application never terminates
printer TLS at all:

- `Listeners:PrinterPort` is plain HTTP whichever way the deployment is configured. There is no
  branch on any setting.
- `PrusaConnect:PrinterTls` decides whether a leaf is minted **for the proxy to present**, and what
  goes in a printer's ini. It does not decide what this process binds.

Writing small WebSocket frames from the application was tried, and reverted. It cannot work through
a proxy: nginx writes what it read upstream, so two small frames arriving in one read become one
oversized record. The cap belongs in the terminator.

## What a replacement terminator must do

Being OpenSSL-based buys nothing on its own — it has to be **configured**. Go's `crypto/tls` cannot
do this at all, which rules out Caddy and Traefik for this port. Read
[`nginx/homespool-printer.conf`](../nginx/homespool-printer.conf) before substituting anything.

| requirement | why |
|---|---|
| Exactly one certificate, no chain appended | firmware's `x509_crt_check_ee_locally_trusted` requires it; an appended authority fails as though the protocol were broken |
| One ciphersuite, TLS 1.2 | `ECDHE-ECDSA-AES128-GCM-SHA256`; the printer refuses TLS 1.3 |
| No response buffering | 256 KiB transfer chunks otherwise sit in the proxy |
| **Two** record caps, not one | see below — this is the one people miss |

### The two record caps

`ssl_buffer_size` covers ordinary responses. **`proxy_buffer_size` on `location = /p/ws` covers the
WebSocket**, and it is a separate directive because an upgraded connection is *tunnelled*: nginx
stops using its own output path and writes each upstream read straight to `SSL_write`, so **one
upstream read becomes one TLS record** and `ssl_buffer_size` no longer applies. Both are `1000` in
the shipped config.

Miss the second and everything looks healthy — pages load, `/p/*` responds — while every file
transfer fails.

### The failure signature

Nothing logs an error at either end, and `nginx -t` passes. What you see instead:

- the printer's screen sits at 0%
- the application log fills with `/p/ws responded 101` over and over, as the printer drops the
  connection and reconnects into the same failure

Only a packet capture shows the cause. On the printer path, a record size that is not roughly 1024
plus cipher overhead means the cap is not being applied.

## Do not put a second proxy in front of the printer port

The requirements above are the whole reason. A general-purpose proxy that buffers responses, presents
a chain, or negotiates a modern ciphersuite breaks the printer path in ways that read as protocol
bugs. The shipped proxy answers `404` to `/p/` on the people-facing port for the same reason.
