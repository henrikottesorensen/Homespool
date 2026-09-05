# Homespool

**A self-hostable alternative to Prusa Connect.** Homespool lets you run Connect-style printer
management on your own hardware — enrolment, live telemetry, a print queue, file uploads, cameras
and remote control — without your models or your printers' data ever leaving your network.

The name carries three meanings on purpose: a filament *spool*, a print *spooler* in the lpd/CUPS
sense (which is exactly what this is), and *home*, for the self-hosting that is the whole point.

## What it does

- **Speaks the Prusa Connect protocol to your printers.** Printers with the Connect client in their
  firmware (the Buddy family — MINI, MK3.5/3.9/4, XL, including multi-tool machines) connect
  directly to your Homespool server, over the WebSocket transport or the older HTTP transport for
  firmware built without WebSocket support. Homespool is also tested against Prusa's Python Connect
  SDK, so pre-Buddy printers (MK3S+ and kin) attached through a Raspberry Pi running PrusaLink —
  which is built on that SDK — are expected to work too, though that combination is unconfirmed on
  real hardware. HT-90 compatibility is unknown.
- **Print queue and history** — queue jobs per printer; the printer pulls the next one when it is
  ready.
- **Live telemetry** — temperatures, progress and printer state, stored in SQLite with configurable
  retention, with a job-scoped temperature graph on the printer page.
- **File management** — upload gcode through the web UI, or send straight from PrusaSlicer:
  Homespool works as a print host (add it as an *OctoPrint* physical printer pointing at
  `/compat`).
- **Cameras** — snapshots and live WebRTC view via a bundled [go2rtc](https://github.com/AlexxIT/go2rtc)
  sidecar; RTSP, ONVIF, HTTP and USB (V4L2) cameras all work, added from the web UI.
- **Remote control** — preheat and cool down per filament type, unload filament, set a printer
  ready — guarded by an allowlist of exactly the gcode the UI can send.
- **Accounts, teams and tokens** — ASP.NET Identity accounts with optional external OpenID Connect
  sign-in, team-based sharing with a capability model, and personal access tokens for the API.
- **TLS by default** — the stack ships an nginx front end that terminates TLS for browsers on one
  port and for printers on another, with a certificate authority Homespool mints itself for the
  printer side. Provisioning a printer is a zip file on a USB stick.
- **Localised** — the UI ships in English and Danish, and is built so another language is a
  translated resource file and one line in the supported-language list — never a schema migration.

Scale target is a home or workshop: one to tens of printers, not a multi-tenant cloud.

## Installation

### Requirements

- Docker with the compose plugin, on any machine your printers can reach (a personal server, a NAS
  that runs containers, a Raspberry Pi).
- Nothing else — the images are built from this repository, and the database is SQLite on a Docker
  volume.

### Docker compose (the primary target)

```bash
git clone https://github.com/henrikottesorensen/Homespool.git
cd Homespool
./setup-env.sh
./build.sh
docker compose up -d
```

- `./setup-env.sh` is an interactive wizard that writes the handful of `.env` settings a deployment
  cannot guess — hostnames, the printer address, camera credentials — and leaves everything else
  alone. It shows what it will change before writing. You can skip it and edit `.env` by hand;
  see [Configuration](#configuration) below.
- `./build.sh` builds the two container images with the git commit stamped in, so `--version` and
  the admin pages can say what is running. A bare `docker compose build` also works, but produces
  images that report an unknown commit.

Then browse to `https://<your-host>/`. Until an administrator account exists, every page redirects
to `/setup`, which asks for a **one-time setup token**. The token is printed in the application's
log on startup:

```bash
docker compose logs homespool
```

Enter it to create the first administrator account. The token lives in memory only — it is never
written to disk, a restart mints a fresh one (so the last one logged is the one that counts), and
once an administrator exists, first-time setup is disabled for good.

Add printers under **Printers → Add printer**, which produces a provisioning bundle — a zip holding
`prusa_printer_settings.ini` and the certificate — that you unzip onto a USB stick and load on the
printer.

**Set `PRINTER_HOST` before the first start if you can.** The printer-facing certificate is issued
once, on the first run, and covers the addresses the machine has at that moment plus whatever
`PRINTER_HOST` names — setting it first means your printers' address is covered by construction.

### Raspberry Pi SD-card image

For a turnkey appliance, `pi/build.sh` builds a complete SD-card image — Debian trixie (arm64),
Docker and the Homespool stack with the container images already on the card. Boot it, wait, browse to
`http://homespool.local`. One image runs every 64-bit Pi (Pi 3 through Pi 5). See
[pi/README.md](pi/README.md).

### Upgrading

```bash
git pull
./build.sh
docker compose up -d
```

The database and certificates live on named Docker volumes and survive container replacement. Keep
the data volume on local disk — SQLite's locking is unreliable on NFS/CIFS shares.

**The most sensitive file on that volume is the printer CA's private key**
(`certificates/ca.key.pem`): every provisioned printer trusts that CA and nothing else, there is no
revocation, and replacing it means a USB visit to every printer. The key is always encrypted under
`CA_PASSPHRASE` (which `setup-env.sh` generates; the server refuses to start without one), so a
copied backup of the data volume alone cannot yield it — back up `.env` too, and **separately**
from the data volume: the passphrase cannot be regenerated, and a backup holding both halves has
defended nothing. The same passphrase encrypts `certificates/dataprotection.key.pem`, the key
behind the certificate that protects the key ring for sign-in cookies and password-reset links, so
a copied volume cannot mint a session for anyone either.

## Configuration

Configuration lives in two places, deliberately:

1. **`.env`** holds what the compose stack itself must know — ports, hostnames, volume-adjacent
   credentials. Copy [.env.example](.env.example) (or run `./setup-env.sh`); every setting carries
   a default in `compose.yaml`, so `.env` only needs what differs on your machine.
2. **The application's settings page** (as an administrator) holds everything else — mail, telemetry
   retention, upload and attempt limits, camera timings. These are kept in `data/settings.json` on
   the data volume, so they survive a redeploy and never require editing compose files.

### The `.env` settings that matter

| Setting | What it is |
|---|---|
| `USER_HOSTS` | The names people browse to, semicolon-separated. Both nginx and the app refuse any other `Host`, and every name goes into the self-signed browser certificate. |
| `PRINTER_HOST` | The address printers use to reach this machine — its own LAN name or address, not a proxy's. Required before **Add printer** will produce a provisioning snippet. |
| `PORT` / `HTTPS_PORT` | Where the proxy publishes HTTP and HTTPS for people. HTTP redirects to HTTPS. IPv4 only, as is every port the stack publishes; `compose.yaml` says why. |
| `PRINTER_PORT` | The TLS port printers connect to (default 15443). Written into every provisioning bundle, typed by no one. |
| `TRANSFER_PORT` | Port for file downloads on the pre-WebSocket transport. Plain HTTP by design — the file body is already encrypted, but its integrity isn't verified, so don't expose this port beyond your LAN. |
| `TZ` | The IANA timezone timestamps are rendered in. Containers default to UTC, which is rarely right for a machine in a house. |
| `GO2RTC_USERNAME` / `GO2RTC_PASSWORD` | Credentials for the camera sidecar's API — required if you want cameras, ignored otherwise. `setup-env.sh` generates them. |
| `CA_PASSPHRASE` | Encrypts the printer CA's private key at rest, and the certificate that encrypts the sign-in key ring. Required — the server refuses to start without one rather than store either key in the clear; `setup-env.sh` generates it. **Never change or lose it once set** — the server refuses to start rather than mint a CA that strands your printers, or a key-ring certificate that signs everyone out. |
| `PROXY_SUBNET` / `PROXY_NETWORK` | The stack's internal Docker network and the range whose forwarded headers are trusted. Change both together only if the default collides with your LAN. |

[.env.example](.env.example) documents every setting in full, including the WebRTC overrides for
deployments behind a router or tunnel.

### TLS

- **Browsers** get a self-signed certificate generated on first start, covering every name in
  `USER_HOSTS`. To use a real certificate, put `homespool.crt` and `homespool.key` into the
  `homespool-proxy-certs` volume and restart the proxy.
- **Printers** get a certificate Homespool mints itself, delivered on the provisioning USB stick,
  so the printer connection is verified TLS out of the box with no public CA involved.
- Bringing your own reverse proxy (Traefik, Caddy, your nginx) is supported for the people-facing
  half — point `PROXY_NETWORK` at it. The printer-facing half is **not** a normal reverse-proxy
  job, and a generic proxy will break it: the firmware's TLS stack holds one kilobyte of plaintext
  at a time, so every TLS record must be capped at 1000 bytes — on the ordinary path *and* through
  the WebSocket tunnel. This limit is extremely easy to get wrong, because nothing tells you it
  exists: the printer never advertises it, proxy defaults are sixteen times too big, and the
  failure shows up as file transfers dying at 0%, not as anything naming record size. On top of
  that, the leaf certificate must be presented alone with no chain, and exactly one protocol and
  ciphersuite works (ECDHE-ECDSA-AES128-GCM-SHA256 over TLS 1.2 on P-256). The
  shipped nginx encodes all of this; read
  [nginx/homespool-printer.conf](nginx/homespool-printer.conf) before substituting anything for
  that half.

### Mail

Optional. Configure an SMTP server on the settings page and Homespool sends confirmation, invite
and password-reset mail. Leave it unconfigured and new accounts are created already confirmed, with
password reset unavailable.

## Building from source

Development needs the .NET 10 SDK (10.0.302 or newer; see `global.json`):

```bash
dotnet build Homespool.slnx
dotnet test Homespool.Host.Test/Homespool.Host.Test.csproj
dotnet test Homespool.Host.E2ETest/Homespool.Host.E2ETest.csproj
```

`dotnet test` on the whole solution additionally needs a [Mailpit](https://mailpit.axllent.org/)
instance running — the integration-test project exists to talk to a real SMTP server. The solution
also includes a FakePrinter library and CLI that emulate a Buddy-firmware printer against a running
server, for development without hardware.

## License

[GNU AGPL v3](LICENSE.md).
