# Homespool

A self-hosted 3D printer spooler. Your models stay on your own server.

Homespool speaks the Prusa Connect protocol, so a Prusa printer can be pointed at your own machine
instead of `connect.prusa3d.com` — no cloud account, and nothing about what you print leaves your
network. It accepts sliced files, queues them, sends them to printers, and shows you what the
printers are doing.

---

## What it does

Everything below has been run against real hardware (an MK3.5) as well as the test suite.

- **Enrols printers** two ways: a provisioning bundle you unzip onto a USB stick, or the
  registration-code flow the printer's own wizard uses.
- **Receives and stores telemetry** — live state, a history of samples, and the printer's events —
  in SQLite, with a retention sweep for the samples.
- **A page per printer** — live state, the queue, print history, cameras pointed at it, and recent
  samples and events.
- **A print queue per printer.** A loop sends the next file, waits for the printer to take it, and
  starts the print. When it cannot proceed it **holds and says why** rather than skipping ahead.
- **Print history** — every print recorded, including the ones that never started.
- **Commands** — Pause, Resume, Stop and Set Ready, correlated back to the printer's own answer, so
  a refusal shows the printer's reason.
- **Preheat and cool down** from a printer's page, per filament type, using the temperatures the
  printer's own preheat menu would choose.
- **Print files** — upload, rename, delete, queue, or send straight to a printer, and browse what
  is already on the printer's own USB stick.
- **Cameras** — a webcam plugged into the server, or an RTSP, ONVIF or HTTP camera on your network,
  shown on the printer's page and kept current while somebody is watching.
- **Sending from PrusaSlicer** — its OctoPrint host type uploads straight into a printer's queue.
- **A JSON API** at `/api/v1`, authenticated by sign-in cookie or personal access token. A token is
  scoped when you create it, so a key for a slicer can upload and print without being able to delete
  anything — see [Capabilities](docs/capabilities.md).
- **User accounts** — invite-only signup, admin bootstrap, 2FA, and teams whose members hold named
  capabilities on the printers and cameras that team owns.
- **TLS from the first start**, with printers and people on separate ports and separate
  certificates.
- **Health checks and alerting** — `/health`, an administrator banner, and email when a check
  starts failing.
- **Three languages** — British English, American English and Danish, chosen per account or taken
  from your browser.
- **A Raspberry Pi image** with the whole stack on it — see [A Raspberry Pi image](#a-raspberry-pi-image).

### What it does not do

- **Most of the protocol's commands are not exposed.** Pause, Resume, Stop, Ready, Unready and Idle
  are on the API; the first four are buttons; preheat and cool down are page-only. Nothing else has a
  caller, and there is no handling of commands the printer sends *to* the server (dialogs, for
  instance).
- **No arbitrary gcode**, deliberately. What can be sent goes through an allowlist, because
  firmware's `M997` reflashes the mainboard from a file on `/usb/` and validates nothing — "upload a
  file" plus "send any gcode" would add up to arbitrary firmware on someone's printer.
- **No charts, and only cameras update themselves.** A camera panel refreshes while you watch it;
  everything else on a printer's page renders on load.
- **No transfer progress on the Files page** while a file is on its way to a printer.
- **No file dedup or expiry.** A file stays until you delete it.
- **No password reset without SMTP**, and no admin-side reset.
- **Team membership is set at invitation** and there is no page to change it afterwards.

### Supported printers

Only printers running **Buddy firmware** — MK4, MK3.5, XL, MINI, Core One — which speak the
WebSocket transport.

**An MK3S+ driven by a Raspberry Pi (PrusaLink / the Python SDK) is not supported.** That setup uses
an HTTP-only transport (`POST /p/telemetry`, `POST /p/events`) which is not implemented.

Scale assumption throughout is **one to tens of printers**, self-hosted. This is not a multi-tenant
service.

---

## Running it

### Docker Compose

```bash
./setup-env.sh            # asks a handful of questions and writes .env
docker compose up --build
```

`setup-env.sh` exists because one setting — the address printers use to reach this machine — has no
sensible default and is expensive to get wrong. It detects the addresses this machine can be reached
on, excludes the ones no printer can route to, and writes `.env`, asking about ports and mail only if
you want them and generating the camera sidecar's credential without asking.

It **patches** `.env` rather than regenerating it, so it is safe to re-run later to add SMTP or
repoint an address: comments, blank lines and any setting it did not ask about are left as they were.
`--dry-run` shows what it would change; `--help` lists the rest.

If you would rather do it by hand, `cp .env.example .env` and edit — the file documents every
setting, and every one has a default in `compose.yaml`, so `.env` only needs to hold what differs.

**On Windows with Docker Desktop**, run `setup-env.cmd`. Windows has no bash, so it runs the same
script inside Homespool's own image (build it first; it will say so) and asks a small PowerShell
script for the host's addresses, since a container cannot see the host's LAN. Use the `.cmd` rather
than calling `setup-env.ps1` directly: it passes `-ExecutionPolicy Bypass` for that one process, so
you are not asked to weaken a machine-wide setting. By hand:

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup-env.ps1
```

**On Windows without Docker Desktop**, install WSL2 and Docker Engine inside the distro and run
`./setup-env.sh` there. It recognises WSL and asks Windows for the addresses itself.

That brings up three containers: the application, an nginx that terminates TLS for both of its
audiences, and a [go2rtc](https://github.com/AlexxIT/go2rtc) sidecar that talks to cameras. The
database lives in a named volume (`homespool-data`) so it survives container replacement. The sidecar
costs nothing when no camera is configured — it publishes no port and idles.

**Everything is served over TLS from the first start, and the audiences are kept apart** — with one
deliberate exception, and it is plain because it has to be.

| | who | port | certificate |
|---|---|---|---|
| **people** | pages, `/api` | `443` (`HTTPS_PORT`), with `80` redirecting to it | self-signed on first start; replace it with your own |
| **printers** | `/p/*`, nothing else | `15443` (`PRINTER_PORT`) | minted by this deployment, and the one on the USB stick |
| **transfers** | `/f/*`, nothing else | `15080` (`TRANSFER_PORT`) | **none — plain HTTP by design.** A printer built without websockets fetches files here over a raw socket it cannot make TLS; the body is AES-CTR ciphertext, keyed by a per-transfer key that only ever travelled over the printer's own channel. Nothing checks the body's *integrity*, so treat this port as LAN-only. |

**None of the application's own ports are published.** nginx reaches all three over the compose
network and is the only thing that can. Inside the app, `/p/*` exists on the printer listener alone,
`/f/*` on the transfer listener alone, and every other route exists everywhere else, so a request on
the wrong port is answered `404`.

**Set `PRINTER_HOST` in `.env` before the first start** — which `setup-env.sh` does for you. There is
no way to infer your server's externally-reachable address from inside the container, so USB-key
provisioning cannot produce a usable bundle until it is set — and the printer certificate is issued
**once, on the first run**, covering every address the machine can see at that moment plus whatever
`PRINTER_HOST` says. Setting it later is fine as long as it is one of the addresses that were
detected; if not, **Admin → Printer certificate** shows what the certificate covers and reissues it.

**The proxy is not optional on the printer side.** Prusa firmware can only hold very small TLS
records, and .NET cannot produce them, so nginx terminates the printer's TLS. Nothing to configure —
the shipped stack handles it. If you plan to **replace the proxy**, read
[Printer TLS](docs/printer-tls.md) first: that half has requirements a general-purpose proxy will not
meet by default, and getting them wrong breaks file transfers while everything else looks healthy.

**The browser will warn on first use.** The certificate the proxy generates is signed by nobody;
serving your credentials in clear while you go and obtain a real certificate would be the worse
default. To replace it, put `homespool.crt` and `homespool.key` into the `homespool-proxy-certs`
volume and restart the proxy. `nginx/homespool.conf.template` has a commented HSTS line to uncomment
once you have one.

**Already run Traefik, Caddy or your own nginx?** Put it in front of the app's `8080` and point
`XForwarded__KnownNetworks` at its network — the shipped proxy is a default, not a requirement, **on
the people-facing side**. Keep the shipped proxy for the printer port, or read
[Printer TLS](docs/printer-tls.md) before replacing it; Traefik and Caddy in particular cannot serve
that port at all.

> **Two things will break a deployment quietly, so get them right first:**
>
> - **Do not chain another reverse proxy in front of the printer port.** One more hop that buffers
>   responses or presents a certificate chain breaks transfers in ways that read as protocol bugs.
> - **Do not put the data volume on NFS, CIFS or a NAS share.** SQLite's WAL locking is unreliable
>   over network filesystems and will eventually corrupt the database. Use a local Docker volume or a
>   bind-mount to local disk.

### From source

Requires the **.NET 10 SDK**, 10.0.302 or newer (`global.json` sets that as a floor and rolls forward
to the newest feature band installed).

```bash
dotnet tool restore
cd Homespool.Host && dotnet libman restore && cd ..
dotnet run --project Homespool.Host
```

`dotnet libman restore` fetches the client-side assets (Bootstrap, jQuery) into `wwwroot/lib/`, which
are gitignored. It must be run from `Homespool.Host/`; Rider, Visual Studio and the Docker build do it
automatically. The database is created and migrated on first start.

> **This serves people, not printers.** Run from source, the printer listener is plain HTTP on
> `15443` with nothing in front of it, so a printer configured with `tls = true` fails to connect. For
> printer work from source you need either the Compose stack in front of it, or
> `PrusaConnect__PrinterTls=false` with printers told the same — the testing path described under
> [`PrinterTls`](#turning-printertls-off-and-when-that-is-legitimate). The fake printer under
> [Testing without a printer](#testing-without-a-printer) needs neither.

---

## First run

There is no open signup and no default password. On first start — while no administrator exists —
the server prints a one-time setup token to its log:

```
No administrator account exists yet. Complete first-time setup by opening /setup and
entering this one-time token:

    <a fresh 32-character token, printed here>
```

Open `/setup`, enter that token along with the account details you want, and you have an
administrator. Until then every page redirects to `/setup`, so nobody can reach the server in the
window before you have claimed it (the printer protocol under `/p` is exempt).

The token is held **in memory only** and regenerated on every restart. If you miss it, restart the
server and read the new one. Once an administrator exists, `/setup` returns 404 permanently.

Everyone else joins by invitation: **Invites → Create invitation**, which also chooses the team the
new account lands on. With SMTP configured the invite is emailed; without it, copy the link and pass
it on yourself.

---

## Adding a printer

Two ways, depending on whether the printer can already reach the server.

### USB key (works before the printer has ever connected)

1. **Printers → Add printer (USB key)**, give it a name and location.
2. Choose the address this printer should use — the list is the names your printer certificate
   covers, defaulting to `PrinterHost`.
3. **Download provisioning bundle.** This is the only time the token is available.
4. Unzip it onto the **root** of a USB stick — not into a folder — and load it from the printer's
   own menu: *Prusa Connect → Load Settings*.

```
prusa_printer_settings.ini     the [service::connect] section, with your token
connect.der                    the certificate authority the printer must trust
README.Bundle.md               these instructions, for whoever opens the zip
```

Nothing is transcribed by hand, and that is the point: a `;` comment (this parser treats it as an
error), an omitted key (`token`'s default de-enrols the printer), a PEM renamed `.der`, a mistyped
code — all fail in ways that look like protocol bugs, and a generated file cannot make any of them.
Only `[service::connect]` is written; Wi-Fi credentials are yours, so add your own `[network]` section
to the same file if you need one. *Read the ini instead* on the same page shows exactly what the
file will contain.

> **`custom_cert = 1` replaces the printer's trust store rather than adding to it.** While it is set,
> that printer cannot talk to Prusa Connect. The same warning is written into the ini itself.

The printer enrols itself the moment it first connects. Until then it shows as *Awaiting USB
provisioning*, and you can reissue the token if the stick was never written — the old one stops
working immediately. Requires `PrusaConnect:PrinterHost` to be set; the page tells you if it is not.

### Registration code (printer contacts the server first)

The flow Prusa's own firmware wizard uses: point the printer at your server, and it displays a short
code for you to claim.

A printer fresh out of the box only knows Prusa's own servers, and **the hostname field under
*Settings → Network → Prusa Connect* is read-only** — the only way to repoint it is loading a
`prusa_printer_settings.ini` from USB. So this flow needs a USB step too, just a lighter one — the
host/port/tls lines, no token:

```ini
[service::connect]
hostname = printers.example.com
port = 15443
tls = true
```

Load it (**Prusa Connect → Load Settings**), *then* **Add Printer to Connect** talks to your server
and displays a claimable code. This is worth doing over full USB-key provisioning when the person
with the USB stick is not the person who should claim the printer in the app — full provisioning
needs `CanManage` on a team up front.

> **Ignore the QR code the printer shows.** It is hardcoded to `connect.prusa3d.com`. The code is also
> shown as text underneath — that is the part you want.

**Printers → Claim printer**, enter the code, give it a name and location. The printer polls until you
do, then receives its token automatically. Codes expire after **30 minutes** by default. Repeatedly
submitting codes that match nothing backs your account off, doubling from 30 seconds up to an hour;
it self-heals.

---

## Print files

**Files** in the navigation is a per-user store: upload a sliced model — `.gcode`, `.bgcode`, `.gco`
or `.bgc` — then rename it, delete it, queue it, or send it to a printer you have `CanUse` on. Files
are yours: the store is a tree per user, and names are unique per user.

The same store is reachable over the API by name:

```
GET    /api/v1/files                 everything you have uploaded
PUT    /api/v1/files/{fileName}      upload
GET    /api/v1/files/{fileName}      download it back
PATCH  /api/v1/files/{fileName}      rename
DELETE /api/v1/files/{fileName}      delete
```

Sending a file to a printer and printing it are **separate calls**, because a file can be on a
printer without this server having put it there:

```
POST /api/v1/printers/{uuid}/files                 send one of your files to the printer
GET  /api/v1/printers/{uuid}/storage/usb/{path}    browse the printer's USB stick
POST /api/v1/printers/{uuid}/print                 print a path already on the printer
```

The send answers as soon as the printer *accepts* the transfer, not when it finishes — the printer
then pulls the bytes at its own pace, and a full-size model takes minutes. Browsing the stick is gated
on `CanUse` rather than `CanRead`, since it makes the printer go and do work.

---

## Print queue

Each printer has **one queue, shared by everyone** who can use it. A background loop watches the
front of it: send the file, wait for the printer to confirm it has it, start the print, move on.

`CanUse` on the printer's team lets you add, reorder and cancel entries; `CanRead` lets you watch. The
queue is on the printer's page, on the Files page, and at:

```
GET    /api/v1/printers/{uuid}/queue                    the queue, in order
POST   /api/v1/printers/{uuid}/queue                    add a file to the back
PATCH  /api/v1/printers/{uuid}/queue/{id}               move it
DELETE /api/v1/printers/{uuid}/queue/{id}               take it out
```

**When the loop cannot proceed it holds and says why**, rather than skipping to something that would
work — a queue that quietly reorders itself around an obstacle is a queue you cannot reason about.
Three of the reasons clear themselves:

| it says | what is happening |
|---|---|
| Sending *file* to the printer | a transfer is in flight — firmware allows only one at a time |
| Waiting for the printer to confirm the file | the bytes arrived, but the printer has not yet named a path to print |
| *(nothing)* | a print has been commanded and the printer still says `READY` for a few seconds |

Two are waiting for a person:

| it says | what is happening |
|---|---|
| Waiting for the printer to be made ready | the printer is not `Ready`, **most often a finished print nobody has cleared** |
| *(a banner with the space needed and free)* | the file at the front will not fit on the drive |

The first of those is the one to understand before you meet it, because a correctly working queue
sitting behind a finished print looks exactly like a broken one.

### Setting a printer ready

**Taking the print off the bed is not enough, and neither is dismissing the screen** — firmware then
reports `Idle`, not `Ready`. Readiness is a separate declaration that the sheet is clear, and only a
person makes it; the loop never decides it for you, because the failure mode is printing onto a
finished part, which the firmware will happily do.

There are three ways to declare it: *Set Ready* at the printer's panel,
`PUT /api/v1/printers/{uuid}/command/ready`, or a **Set Ready** button on the printer's page. The
button is **off by default, per printer**: turn on *Allow setting ready from this page* only if
anyone reading the page can tell whether the sheet is clear — a camera aimed at the sheet, in
practice. It opens a prompt with the camera's latest snapshot beside the question, and the confirm
button carries the assertion itself: *The print sheet is clear*. Without a camera the same prompt
says so out loud.

**Print history** sits beside the queue. Every print is recorded, including the ones that never
started, with the file's name as it was at the time.

---

## Cameras

**Cameras** in the navigation adds a camera and points it at a printer; its picture then appears on
that printer's page. A cheap USB webcam works. So does Prusa's Buddy Camera, as an ordinary `rtsp://`
source like any other.

- **A network camera** is an address you type — `rtsp://`, `rtsps://`, `http://`, `https://`,
  `rtmp://` or `onvif://`. An ONVIF camera is given as `onvif://user:pass@192.168.1.50`, and the
  stream behind it is found for you. Needs `CanManage` on the team it belongs to.
- **An attached camera** is chosen from what is plugged into the server, and needs
  **administrator** — a device is the machine's rather than any one team's. The picker lists only
  devices nobody has claimed, and deleting a camera hands its device back.

A [go2rtc](https://github.com/AlexxIT/go2rtc) sidecar does the talking to cameras; Homespool
configures it when you add one. Its port is not published, and every viewing path is proxied through
Homespool so the camera's own permission check applies. `GO2RTC_USERNAME` and `GO2RTC_PASSWORD` in
`.env` are its credential — `setup-env.sh` generates them; set **both or neither**.

**Frames are fetched only while somebody is looking**, so a page left open stays current and a
camera nobody is watching costs nothing. An attached camera is grabbed at roughly half a second, a
1080p H.264 network camera at two to three. Neither is a live stream: this is "is the print still all
right", not video.

> **A frame past `MaxAgeSeconds` is thrown away rather than captioned.** A day-old photograph of a
> clear print bed looks exactly like a current one, and an age label is no protection because people
> look at the picture. Past that age the page says it is capturing, and shows nothing.

---

## API access

`/api/v1` is this application's own API; only `/p/*` owes Prusa's protocol anything. In Development
the OpenAPI document is at `/openapi/v1.json` with a [Scalar](https://scalar.com) UI at `/scalar/v1`.

It accepts either the sign-in cookie or a **personal access token**. Mint one under **Account → API
tokens**; it is shown **once**, and revocable in one click from the same page.

```bash
curl -H "Authorization: Bearer hs_..." https://homespool.example.com/api/v1/printers
```

The `hs_` prefix makes a leaked token greppable in a `.env` or a shell history. An unauthenticated
`/api` request is answered `401`/`403` rather than redirected to the login page.

---

## Sending from PrusaSlicer

Homespool answers enough of OctoPrint's upload protocol for **Send G-code** to work.

A printer's page carries a **Send from a slicer** box with the address to paste. In PrusaSlicer,
under *Printer Settings → Physical Printer*, set **Host Type** to **OctoPrint**, paste that as the
hostname, and use a [personal access token](#api-access) as the API key.

**Upload and Print adds the file to the queue rather than starting it** — only a person marks a
printer ready. A name that already exists is refused rather than overwritten; rename it in the send
dialog.

---

## Configuration

Standard ASP.NET Core configuration: `appsettings.json`, environment variables, user secrets. In
Docker, use the `__` (double underscore) form, e.g. `PrusaConnect__PrinterHost`.

### Time zone

Set `TZ` in `.env` to an IANA name — `Europe/Copenhagen`, `America/Denver`. It defaults to `UTC`.
It matters because times are rendered **on the server**: print history, uploads, invitations and API
tokens are formatted before the page is sent, and an invitation email states its expiry the same
way. Presentation only — times are stored as absolute instants, so setting it later re-renders
existing history correctly.

### Language

Nothing to configure. Homespool ships **English (UK)**, **English (US)** and **Dansk**, and picks one
per request: the account's choice under **Account → Language** first, then this browser's last
choice, then `Accept-Language`. The two Englishes differ in formatting rather than words —
`09/03/2026` and a 24-hour clock against `3/9/2026` and a 12-hour one; an unqualified
`Accept-Language: en` lands on British.

### `PrusaConnect`

| Setting | Default | Purpose |
|---|---|---|
| `PrinterHost` | *(empty)* | The hostname printers use to reach this server. **Required for USB-key provisioning.** |
| `PrinterPort` | `15443` | Port written into the provisioning ini — the host side of the printer port mapping. Not 443: that belongs to the people-facing proxy. |
| `TransferPort` | `15080` | Port written into `START_ENCRYPTED_DOWNLOAD` — where a printer without websockets fetches a file from. Plain HTTP, and always sent explicitly: firmware would otherwise fetch from its enrolled port, rewriting 443 to 80. |
| `PrinterTls` | `true` | Whether printers reach this deployment over TLS — the `tls` line in the ini **and** whether a certificate is issued for the proxy to present, so the two cannot disagree. See below. |
| `RegistrationCodeLifetimeMinutes` | `30` | How long a registration code stays claimable. Deliberately tighter than Prusa's 24 h; claiming is done standing at the printer. |
| `MaxIncomingMessageBytes` | `1048576` | Bytes a printer may accumulate without completing a message before the connection is closed. About eleven times the largest message measured (a `FILE_INFO` with a thumbnail); tripping it is logged with the printer and the byte count. |
| `MaxFailedClaimAttempts` | `5` | Unrecognised codes an account may submit before it is backed off. |
| `ClaimLockoutBaseSeconds` | `30` | First backoff once that is passed. Doubles per further failure. |
| `ClaimLockoutMaxSeconds` | `3600` | Ceiling on the doubling, so the lockout always self-heals. |
| `CommandResponseTimeoutSeconds` | `10` | How long to wait for the printer's answer to a command before giving up on it. |

#### Turning `PrinterTls` off, and when that is legitimate

`PrusaConnect__PrinterTls=false` issues no certificate, so the proxy has nothing to present and stays
out of the printer path. In Compose it needs the plaintext override as well, which moves the
published printer port off the proxy and onto the application:

```bash
docker compose -f compose.yaml -f compose.plaintext.yaml up
```

It exists for testing — reading the protocol on the wire, and rigs (`rig/`, `tools/slow-db/`) that
drive the printer endpoints with curl and the fake printer. Every printer token then crosses the
network in clear, in both directions, and the server logs a warning saying so at every startup. **Do
not run a deployment this way**, LAN-only included.

### `Listeners`

One listener per credential class, so a leaked credential of one kind reaches no surface belonging to
another. Naming any of these makes Kestrel ignore `ASPNETCORE_URLS`.

| Setting | Default | Purpose |
|---|---|---|
| `PrinterPort` | `15443` | Plain HTTP: `/p/*` and nothing else. Not published to the host; the shipped proxy terminates the printer's TLS in front of it. |
| `TransferPort` | `15080` | Plain HTTP: `/f/*` and nothing else. Not published to the host; the shipped proxy forwards to it *without* TLS, because the printer opens this with a raw socket. |
| `UserPort` | `8080` | Plain HTTP: pages, `/api`, `/health` — everything except `/p/*`. Not published either; same proxy, different port and certificate. |
| `UserHttpsPort` | *(none)* | An HTTPS listener for people, using the ASP.NET development certificate or `Kestrel:Certificates:Default`. Only if this process should serve user TLS itself; it never carries the printer's certificate. |

### `Certificates`

The authority printers trust, minted on first run into `data/certificates` (inside the volume, so it
survives container replacement). `connect.der` is the file that goes on the USB stick. The leaf is
written twice: `printer.pfx` beside the authority, and the same certificate with its key in PEM under
`data/proxy-certificates`, a **separate volume** that holds only the leaf — the authority's private
key never goes near the container facing the network. The PEM file holds the leaf **alone, no chain
appended**: the firmware requires exactly one certificate presented.

The same directory holds `dataprotection.pfx`, which encrypts the ASP.NET Data Protection key ring
at rest — the keys behind sign-in cookies, antiforgery tokens and password-reset links.

**These private keys are the most sensitive secrets in the deployment.** `custom_cert` replaces the
firmware's trust store wholesale, so the printer CA is each provisioned printer's *entire* trust
store, and there is no revocation. Back up `data/`, but not to somewhere you would not put a private
key.

| Setting | Default | Purpose |
|---|---|---|
| `Directory` | `data/certificates` | Where the authority, the leaf and the key-protection certificate live. |
| `ProxyDirectory` | `data/proxy-certificates` | Where the leaf is written in PEM for the proxy. |
| `AuthorityValidityDays` | `5475` (15 years) | Replacing the authority means a USB visit to every printer, so a short life schedules guaranteed pain and mitigates nothing. |
| `LeafValidityDays` | `730` | The leaf can be replaced with a restart, because printers trust the authority rather than the leaf. |
| `KeyProtectionValidityDays` | `5475` | The Data Protection certificate. Nothing verifies it, so an expiry would only ever surface as an outage. |
| `AuthorityName` | `Homespool printer CA` | Cosmetic; only read by a human inspecting `connect.der`. |
| `ContainerNetworks` | `172.16.0.0/12` | Ranges that exist only inside this deployment; addresses in them are never offered as somewhere a printer could reach. `compose.yaml` feeds it from `PROXY_NETWORK`. |

The printer certificate is issued **once**, at first start, and then left alone. When this machine's
addresses move, `/health` and the administrator banner say so, and **Admin → Printer certificate**
shows what the certificate covers against what the machine now has, with a button to reissue. A
reissue needs a **server restart** and nothing at any printer: they trust the authority, which a
reissue does not touch.

### `Smtp`

Entirely optional. With `Host` empty the server runs without outgoing mail: new accounts are created
already confirmed, invitations must be passed on by hand, and password reset is unavailable.

| Setting | Default | Purpose |
|---|---|---|
| `Host` | *(empty)* | Mail server. Empty disables outgoing mail. |
| `Port` | `587` | 587 for STARTTLS, 465 for implicit TLS, 25 plaintext. |
| `UseImplicitTls` | `false` | `true` for implicit TLS on 465. |
| `DisableTls` | `false` | Explicit opt-in to no encryption. Never a silent fallback. |
| `UserName` / `Password` | *(empty)* | SMTP AUTH. Empty username connects without authenticating. |
| `FromAddress` / `FromName` | *(empty)* / `Homespool` | Envelope sender; falls back to `UserName`. |
| `TimeoutSeconds` | `30` | |
| `ProbeOnStartup` | `true` | Connects and authenticates once at boot to report a broken configuration early. Diagnostic only. |

**Never put the SMTP password in `appsettings.json` or `compose.yaml`** — both are committed. Use
`.env` (gitignored), an environment variable, or user secrets.

### `Storage` and `Invitations`

| Setting | Default | Purpose |
|---|---|---|
| `Storage:AutoMigrate` | `true` | Apply EF migrations at startup. Safe only because exactly one process owns the database. |
| `Storage:TelemetryRetentionDays` | `14` | How long telemetry samples are kept. **`0` disables the sweep.** Events are never swept. |
| `Storage:MinimumSampleIntervalSeconds` | `0` | Minimum seconds between stored samples per printer; `0` stores every message (roughly 86k rows a day per printer at 1 Hz, which SQLite does not mind). |
| `Storage:WriteBatchSize` | `500` | Rows buffered before the writer flushes a batch. |
| `Storage:WriteFlushIntervalSeconds` | `2` | Longest a buffered row waits before being flushed. |
| `Storage:BusyTimeoutMilliseconds` | `5000` | SQLite busy timeout. |
| `Invitations:LifetimeHours` | `48` | How long an invitation stays acceptable. |

### `PrintFiles`

| Setting | Default | Purpose |
|---|---|---|
| `Directory` | `data/printfiles` | Where uploaded gcode lives. Under `data/` on purpose: that is what `compose.yaml` mounts as a volume. |
| `MaxUploadBytes` | `536870912` (512 MiB) | Largest upload accepted. An unbounded upload endpoint is a disk-exhaustion primitive. |

The same warning as the database applies: do not put this directory on NFS, CIFS or a NAS share.

### `Cameras`

Cameras themselves are added in the app, not here. This is the sidecar's address and the limits
around it.

| Setting | Default | Purpose |
|---|---|---|
| `StreamServerBaseUrl` | `http://go2rtc:1984` | The sidecar, by service name on the Compose network. |
| `ApiUsername` / `ApiPassword` | *(empty)* | Credential for the sidecar's API. **Both or neither** — a username with an empty password turns its authentication on with an empty key and locks Homespool out too. |
| `RefreshFloorSeconds` | `2` | Shortest gap between two fetches of one camera, so a browser cannot drive the camera as fast as it can answer. |
| `MaxAgeSeconds` | `60` | How old a frame may be and still be shown. Past this it is **discarded**, not labelled. |
| `TimeoutSeconds` | `15` | How long to wait for a camera to answer. |
| `MaxFrameBytes` | `4194304` (4 MiB) | Largest response accepted from a camera. |
| `RefuseLoopbackAndLinkLocal` | `true` | Refuses camera addresses on loopback or link-local. Everything else is allowed deliberately — reaching a camera on your own LAN is the point. |

### `XForwarded`

Which proxy this deployment trusts, and what it is allowed to say about the client. The shipped
`compose.yaml` configures this for its own nginx; you need it only if you replace that proxy.

| Setting | Default | Purpose |
|---|---|---|
| `KnownProxies` | *(empty)* | Proxy addresses whose forwarded headers are honoured. |
| `KnownNetworks` | *(empty)* | Same, as CIDR ranges. |
| `ClientAddressHeader` | `X-Real-IP` | Header carrying the real client address. |
| `ForwardLimit` | `1` | How many proxies deep to trust. |

> **Configure neither and the middleware is not registered at all.** ASP.NET performs its peer check
> only when at least one proxy or network is known; with both lists empty it would honour forwarded
> headers from *anyone*, so the difference between "trust nothing" and "trust everything" has to be
> made by leaving the middleware out. The server warns when it looks like you are behind a proxy and
> have configured neither — the visible symptom otherwise is confirmation and password-reset mail
> that says `http://`.

---

## Development

```bash
dotnet build Homespool.slnx
dotnet test Homespool.slnx
```

Any warning is a build error, and analysers (`EnforceCodeStyleInBuild`, StyleCop, documentation
checks) are on for every project — a red build from a `<see cref>` or a `var` is expected behaviour,
not a broken toolchain.

### Test projects

| Project | What it covers | Needs |
|---|---|---|
| `Homespool.Host.Test` | Fast, self-contained unit and service tests. | nothing |
| `Homespool.Host.E2ETest` | Drives the real ASP.NET Core pipeline via `WebApplicationFactory` — routing, authentication, middleware. | nothing |
| `Homespool.FakePrinter.Test` | The fake printer client itself — that it behaves like Buddy firmware does. | nothing |
| `Homespool.Host.IntegrationTest` | Real SMTP delivery against a live mail server. | a running Mailpit container |

The fourth assumes Mailpit is already running and **will fail if it is not**:

```bash
docker run -d -p 1025:1025 -p 8025:8025 axllent/mailpit
# or, for the STARTTLS tests, which need a certificate:
Homespool.Host.IntegrationTest/start-mailpit-tls.sh
```

The STARTTLS tests **skip** rather than fail when that script has not been run, so a clean clone runs
green; CI runs the script first.

### Testing without a printer

`Homespool.FakePrinter` is a Buddy-shaped client library — it references neither `Host` nor `Model`,
so it cannot accidentally agree with the server about a wire format both got wrong.
`Homespool.FakePrinter.Cli` drives it against a genuinely running server:

```bash
dotnet run --project Homespool.FakePrinter.Cli -- enrol
dotnet run --project Homespool.FakePrinter.Cli -- run
dotnet run --project Homespool.FakePrinter.Cli -- blast
```

`enrol` registers, prints the claim code and polls for the token; `run` behaves like a printer until
Ctrl-C; `blast` floods telemetry with no delays, for backpressure work. `rig/` and `tools/slow-db/`
drive the printer endpoints with curl for the same purpose; both expect the plaintext path — see
`PrinterTls` above.

### Continuous integration

`.github/workflows/build-and-test.yml` runs the suite and then **builds the container images**, which
is the part a laptop cannot check for itself: a warm layer cache keeps succeeding past the point a
clean machine fails.

### Database

SQLite via EF Core, one migration. The schema is regenerated in place rather than stacked while the
project is pre-release, so **a local development database may not match after a pull** — delete
`Homespool.Host/Homespool.Sqlite` and let it be recreated. Do not do this to a database with data you
care about.

---

## A Raspberry Pi image

`pi/` builds an SD-card image with the whole stack already on it — Debian trixie, arm64, Docker, and
the container images baked into the card's Docker store rather than pulled on first boot. Flash it,
boot it, browse to `http://homespool.local`.

```bash
pi/build.sh --ssh-key ~/.ssh/id_ed25519.pub
```

**One card runs every 64-bit Pi.** It carries both kernels, so the same image has booted a 3B, a 4
and a 5; CM4, CM5 and the Zero 2 W share silicon with boards that have, and are untested rather than
excluded.

It needs an **Apple Silicon Mac or an arm64 Linux box** with Docker; `build.sh` refuses to run on x86
rather than quietly spending an afternoon in qemu. [`pi/README.md`](pi/README.md) covers how it fits
together, and what a Pi 3B's radio will and will not do.

**Cameras are the asterisk on the smaller boards.** Everything else is comfortable on a Pi 3, but one
1080p H.264 camera costs about 90% of a core there and falls behind realtime — measured, not
estimated. A Pi 4 pays 41% of a core for the same camera, a Pi 5 about 18%. `pi/decode-bench.sh` runs
the measurement on a board in front of you.

---

## License

Copyright (C) 2025-2026 Henrik O. Sørensen

[GNU Affero General Public License v3.0](LICENSE.md). If you run a modified version as a network
service, the AGPL requires you to offer that version's source to its users.
