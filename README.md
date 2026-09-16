# Homespool

**A self-hosted alternative to Prusa Connect.** Your printers connect to a server in your own
house or workshop instead of Prusa's cloud, and you get the same kind of thing back: a print queue,
live telemetry, file uploads, cameras and remote control, with none of your models or your
printers' data leaving your network.

## Features

- **Print queue and history** — queue jobs per printer; the printer pulls the next one when it is
  ready.
- **Live telemetry** — temperatures, progress and printer state, kept in SQLite with a retention
  you choose, and a temperature graph per job.
- **Files** — upload gcode in the browser, or send straight from PrusaSlicer, which sees Homespool
  as an OctoPrint print host.
- **Cameras** — stills and live WebRTC view through a bundled [go2rtc](https://github.com/AlexxIT/go2rtc)
  sidecar. Any camera go2rtc supports works: a network camera is added by its address (RTSP, HTTP,
  ONVIF and the rest), and a USB camera plugged into the server is picked from a list.
- **Remote control** — preheat and cool down per filament type, unload filament, set a printer
  ready. Only the handful of gcode the UI can send is ever sent.
- **Accounts, teams and tokens** — local accounts, optional sign-in through an external OpenID
  Connect provider, passkeys, team-based sharing with per-capability permissions, and personal
  access tokens for the API.
- **TLS out of the box** — browsers and printers each get their own TLS port and certificate,
  with nothing to buy or configure. A publicly trusted certificate for a name you own is optional.
- **Localisable.** English and Danish translations currently exist.

Scale target is one to tens of printers, not a multi-tenant cloud.

## Supported printers

| Printer | Status |
|---|---|
| Prusa MINI, MK3.5, MK3.9, MK4, XL, Core One — anything running Buddy firmware | Supported, including multi-tool machines. Update to the newest firmware for the model first; see [Printer will not connect](#printer-will-not-connect) |
| MK3S+ and older, through a Raspberry Pi running PrusaLink | Expected to work, untested on real hardware. Homespool is tested against the Python Connect SDK that PrusaLink is built on |
| HT-90 | Unknown |

Both of the Connect client's transports are supported: WebSocket, and the older HTTP transport for
firmware built without WebSocket support.

## Installation

### Requirements

Docker with the compose plugin, on a machine your printers can reach: a home server, a NAS that
runs containers, or a Raspberry Pi. Nothing else. The images build from this repository and the
database is SQLite on a Docker volume.

Keep the data volume on local disk. SQLite's locking is unreliable on NFS and CIFS shares.

### Docker compose

```bash
git clone https://github.com/henrikottesorensen/Homespool.git
cd Homespool
./setup-env.sh
./build.sh
docker compose up -d
```

`setup-env.sh` is a wizard that writes `.env` for you; on Windows, run `setup-env.cmd`. See
[Configuration](#configuration).

`build.sh` builds the two container images with the git commit stamped in, so the admin pages can
say what version is running. `docker compose build` works too, but the images then report an
unknown commit. On Windows, use `docker compose build`.

**Set `PRINTER_HOST` before the first start if you can.** The printer-facing certificate is issued
on the first run and covers the addresses the machine has at that moment plus whatever
`PRINTER_HOST` names. The wizard fills it in.

### First start

Browse to `https://<your-host>/`. Your browser will warn about the certificate, because it is
self-signed; that is expected. Every page redirects to `/setup` until an administrator exists, and
`/setup` asks for a one-time token printed in the application's log:

```bash
docker compose logs homespool
```

Use the last token in the log. A restart mints a fresh one, and once an administrator exists the
setup page is gone for good.

### Adding a printer

Go to **Printers → Add printer** and download the provisioning bundle. Unzip it onto a USB stick,
put the stick in the printer, and load the settings from the printer's menu. The bundle holds the
`prusa_printer_settings.ini` the printer expects and the certificate it needs to trust your server.

If the printer is not on its newest firmware, put the firmware `.bbf` on the same stick and let the
printer install it first. Older firmware cannot load the certificate; see
[Printer will not connect](#printer-will-not-connect).

### Sending from PrusaSlicer

Open the printer's page in Homespool and copy the address shown under **Send from a slicer**. In
PrusaSlicer, add a physical printer with host type **OctoPrint**, that address, and a personal
access token from your account page as the API key.

### Raspberry Pi SD-card image

For a turnkey appliance, `pi/build.sh` builds a complete SD-card image with Debian, Docker and the
Homespool stack already on the card. One image runs every 64-bit Pi.

Before the first boot, put two files on the card's FAT32 partition, the only one a desktop machine
can read:

- `homespool-wifi.txt`, unless the Pi is wired, with the network to join. WPA3-only networks do
  not work on any Pi; a Pi 3 or Zero 2 W needs a WPA2-only network.

  ```
  ssid=MyNetwork
  psk=the passphrase
  ```

- `homespool-login.txt`, to give the `pi` account a way in. It ships locked with no password, and
  Raspberry Pi Imager's own user and Wi-Fi settings do not apply to this image. You need this: the
  setup token is only printed in the container's log.

  ```
  password=<your password>
  sshkey=ssh-ed25519 AAAAC3Nza... you@laptop
  ```

The secrets in both files are blanked once applied. Flash the card, boot, and wait until
`http://homespool.local` answers. Then fetch the setup token over SSH:

```bash
ssh pi@homespool.local 'cd /opt/homespool && docker compose logs homespool'
```

Give the board a static DHCP lease before enrolling a printer, since the printer certificate is
issued for the address the Pi has on first boot. See [pi/README.md](pi/README.md) for the rest.

### Upgrading

```bash
git pull
./build.sh
docker compose up -d
```

The database and certificates live on named Docker volumes and survive container replacement.

### Backups

Back up two things, and keep them apart:

- the `homespool-data` volume, which holds the database, uploaded files, settings and certificates;
- your `.env` file, which holds `CA_PASSPHRASE`.

The passphrase encrypts the private key of the certificate authority every provisioned printer
trusts. Without it the data volume cannot be restored, and it cannot be regenerated: a new CA would
mean a USB visit to every printer. A backup that holds both halves together protects neither.

## Configuration

`./setup-env.sh` is a wizard that writes the few `.env` settings a deployment cannot guess, such as
the names you browse to and the address printers use, and generates the two secrets. It shows what
it will change before writing, leaves everything else alone, and can be rerun any time to change a
setting. On Windows, run `setup-env.cmd` instead; it runs the same wizard in a container. You can
also skip it and edit `.env` by hand, starting from [.env.example](.env.example).

Configuration lives in two places:

1. **`.env`** holds what the compose stack itself must know: ports, hostnames and the two secrets.
   Every setting has a default, so `.env` only needs what differs on your machine.
2. **The Settings page**, as an administrator, holds everything else: mail, telemetry retention,
   upload limits, camera timings. These survive a redeploy without touching any file.

### The `.env` settings that matter

| Setting | What it is |
|---|---|
| `USER_HOSTS` | The names people browse to, separated by semicolons. Any other `Host` is refused, and each name gets its own certificate. |
| `PRINTER_HOST` | The address printers use to reach this machine: its own LAN name or address, never a proxy's. Required before **Add printer** will produce a bundle. |
| `PORT` / `HTTPS_PORT` | Where HTTP and HTTPS are published for people. HTTP redirects to HTTPS. |
| `PRINTER_PORT` | The TLS port printers connect to. Default 15443. |
| `TRANSFER_PORT` | Plain-HTTP port for file downloads on the older printer transport. LAN only; do not expose it to the internet. |
| `TZ` | The IANA timezone timestamps are shown in. Containers default to UTC. |
| `GO2RTC_USERNAME` / `GO2RTC_PASSWORD` | Credentials for the camera sidecar. Required for cameras, ignored otherwise. The wizard generates them. Both lines must be present, even if empty. |
| `CA_PASSPHRASE` | Encrypts the printer CA's private key and the sign-in key ring. Required; the wizard generates it. **Never change or lose it once set.** |
| `PROXY_SUBNET` / `PROXY_NETWORK` | The private network between the proxy and the app. Change both together only if the default collides with your LAN; the wizard checks. |
| `CAMERA_SUBNET` / `CERTS_SUBNET` | The sidecars' own networks. Same rule. |

[.env.example](.env.example) documents every setting in full, including the WebRTC settings for a
deployment behind a router or tunnel and the plaintext listener for a printer that cannot do TLS.

**Changing `GO2RTC_PASSWORD` or `CA_PASSPHRASE` needs `docker compose up -d --force-recreate`.**
Both reach their containers as files, and compose does not recreate a container for a changed file.
Every other setting takes effect on a plain `up -d`.

If you run the application outside compose, set `AllowedHosts` to the names people browse to,
semicolon-separated. On its own, the app answers only `localhost`.

### Ports to open

If a firewall sits between your printers or browsers and the server, open `HTTPS_PORT` and
`PRINTER_PORT`, plus `WEBRTC_PORT` (default 8555) for live camera view and `TRANSFER_PORT` for
printers on the older transport.

### TLS

- **Browsers** get a self-signed certificate on first start, one per name in `USER_HOSTS`. To use
  your own, put `<name>.crt` and `<name>.key` into the `homespool-proxy-certs` volume, one pair per
  name, and restart the proxy. For a certificate from a public authority, obtained and renewed automatically for a
  name you own in DNS, see [acme/README.md](acme/README.md). Nothing needs to be reachable from the
  internet for that.
- **Printers** get a certificate Homespool mints itself, delivered on the USB stick, so the printer
  connection is verified TLS with no public CA involved.
- **HSTS** is opt-in with `HSTS=1`, and is only ever sent on a name with a publicly issued
  certificate.
- **Your own reverse proxy** in front of the people-facing side works: put it on the stack's proxy
  network and pass the browser's `Host` header through unchanged, which Traefik and Caddy do by
  default and nginx needs `proxy_set_header Host $http_host` for. Do not put a generic proxy in
  front of the printer port. The firmware's TLS stack needs a record size and ciphersuite that
  proxies do not use by default, and the shipped nginx handles it; see
  [docs/printer-tls.md](docs/printer-tls.md) before replacing it.

### Mail

Optional. Configure an SMTP server on the Settings page and Homespool sends confirmation, invite
and password-reset mail. Without it, new accounts are created already confirmed and password reset
is unavailable.

## Printer will not connect

The printer verifies Homespool's certificate through the firmware's `custom_cert` mechanism, which
was broken in most firmware before mid-2026: the certificate file is never read, and the printer
shows an error and sends nothing at all. **Update the printer to the newest firmware for its
model** from [Prusa's downloads](https://help.prusa3d.com/downloads); every model's current release
works. The table is for a printer you cannot update right now.

| Printer firmware | TLS to Homespool |
|---|---|
| 6.5.7 and newer | works |
| 6.5.1 – 6.5.3 | broken, update first |
| 6.4.2 | works |
| 6.4.0, 6.4.1 | broken, update first |
| 6.2.0 – 6.3.4 | broken, update first |
| older than 6.2.0 | no custom-certificate support at all, update first |

What the printer's panel says narrows it down:

| The panel says | It means |
|---|---|
| `Bug: TLS error` | The firmware is too old. Update the printer. |
| `TLS error` | The certificate was read but the handshake failed. Check that the bundle came from this deployment and that the address in it still reaches the server. |
| `Bug` | The certificate file on the printer is missing or truncated. Re-provision from a freshly downloaded bundle. |

A printer that sends no network traffic at all, nothing in the server log and nothing in a packet
capture, is failing before it opens a socket. That is the old-firmware defect, not your certificate.

A printer deliberately kept on old firmware can still be connected over a second, unencrypted
listener; see `LEGACY_PRINTER_PORT` in [.env.example](.env.example) for what that costs.

## Further reading

- [docs/capabilities.md](docs/capabilities.md) — sharing printers with other people and scoping
  API tokens.
- [docs/printer-tls.md](docs/printer-tls.md) — what the printer-facing TLS terminator must do.
- [docs/prusa-slicer-integration.md](docs/prusa-slicer-integration.md) — how PrusaSlicer talks to
  a print host.
- [pi/README.md](pi/README.md) and [acme/README.md](acme/README.md) — the Pi image and public
  certificates.

## Building from source

Development needs the .NET 10 SDK (10.0.302 or newer; see `global.json`):

```bash
dotnet build Homespool.slnx
dotnet test Homespool.Host.Test/Homespool.Host.Test.csproj
dotnet test Homespool.Host.E2ETest/Homespool.Host.E2ETest.csproj
```

`dotnet test` on the whole solution also needs a [Mailpit](https://mailpit.axllent.org/) instance
running; the integration-test project talks to a real SMTP server. The solution includes a
FakePrinter library and CLI that emulate a Buddy-firmware printer against a running server, for
development without hardware.

## License

[GNU AGPL v3](LICENSE.md).
