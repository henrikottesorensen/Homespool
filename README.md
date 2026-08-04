# Homespool

A self-hosted 3D printer spooler. Your models stay on your own server.

Homespool speaks the Prusa Connect protocol, so a Prusa printer can be pointed at your
own machine instead of `connect.prusa3d.com` — no cloud account, and nothing about what you
print leaves your network.

---

## Status: early, and uneven

A work in progress. A printer enrols, connects, and streams telemetry that is **stored** and
shown back to you; Pause, Resume and Stop are sent from the web UI and answered by the printer
itself. Those parts have been run against a real MK3.5.

**Maturity varies a lot by feature, so read the second list as carefully as the first.** The queue
is the newest part and has now run unattended on real hardware — but on two prints, once. The
per-printer view is plain tables rather than charts, and most of the protocol's commands are
unimplemented.

**What works**

- Both printer enrolment channels (registration code, and USB-key provisioning) end to end.
- Printer authentication — fingerprint + token, over plain HTTP and the WebSocket upgrade.
- User accounts: invite-only signup, admin bootstrap, 2FA, teams, and per-team permissions.
- A WebSocket endpoint that accepts printer connections and correctly parses the telemetry
  and event stream.
- **Telemetry persistence** — live state, history samples and events, batched into SQLite,
  with a retention sweep that ages samples out and a shutdown path that drains rather than
  drops what it is holding.
- **A per-printer page** — live state, the queue, print history, and the most recent samples
  and events.
- **A print queue** — one shared queue per printer, advanced by a loop that sends the next file,
  waits for the printer to take it, and starts the print. When it cannot proceed it **holds and
  says why** rather than skipping ahead. It has run unattended on an MK3.5: a person set the
  printer ready and nothing else, and the next queued print started on its own.
- **Print history** — every print recorded, including the ones that never started.
- **Commands** — Pause, Resume and Stop, correlated back to the printer's own answer, so a
  refusal surfaces the printer's reason rather than a guess.
- **Print files** — upload, rename, delete, queue, or send straight to a printer; plus a view of
  what is already on the printer's own drive and USB stick.
- **A JSON API** at `/api/v1`, authenticated by sign-in cookie **or** personal access token.
- **Health checks and alerting** — `/health`, an administrator banner, and email when a check
  starts failing.

**What does not work yet**

- **There is no Ready button.** A printer only takes the next queued job once someone declares the
  bed clear, and **clearing the finished print at the panel does not do it** — firmware then
  reports `Idle`, not `Ready`. Today that declaration is API-only
  (`PUT /api/v1/printers/{uuid}/command/ready`), so between prints you are at the machine or at a
  terminal. A button is deliberately deferred until there is a camera feed: asserting from another
  room that a bed you cannot see is empty is not something this should make easy.
- **The queue is new.** It has produced real prints, but a handful rather than a season's worth.
  Treat a long unattended run as something to watch the first few times.
- **Most commands are not wired.** Six of the roughly thirty command types can actually be sent
  — all six over the API, three of them as buttons. The rest are markers, and nothing maps
  arbitrary *incoming* JSON to a command type, so there is no `GCode` and no dialog handling.
- **No charts, and nothing updates itself.** The per-printer page renders on load and stays
  as it is until you reload it.
- **No password reset without SMTP.** With no mail server configured, a forgotten password
  needs manual intervention, and there is no admin-side reset yet.

### Supported printers

Only printers running **Buddy firmware** — MK4, MK3.5, XL, MINI, Core One — which speak the
WebSocket transport.

**An MK3S+ driven by a Raspberry Pi (PrusaLink / the Python SDK) is not supported.** That
setup uses an HTTP-only transport (`POST /p/telemetry`, `POST /p/events`) which is
deliberately not implemented. Adding it later is a contained piece of work — the message
handling is transport-agnostic by design — but today those endpoints do not exist.

Scale assumption throughout is **one to tens of printers**, self-hosted. This is not a
multi-tenant service.

---

## Running it

### Docker Compose

```bash
cp .env.example .env      # set at least PRINTER_HOST before printers need to reach this server
docker compose up --build
```

That brings up two containers: the application, and an nginx that terminates TLS for both of its
audiences. The database lives in a named volume (`printerservice-data`) so it survives container
replacement.

**Everything is served over TLS from the first start, and the two audiences are kept apart.**

| | who | port | certificate |
|---|---|---|---|
| **people** | pages, `/api` | `443` (`HTTPS_PORT`), with `80` redirecting to it | self-signed on first start; replace it with your own |
| **printers** | `/p/*`, nothing else | `15443` (`PRINTER_PORT`) | minted by this deployment, and the one on the USB stick |

Two certificates, two ports, no overlap: the printer's is an ECDSA leaf signed by an authority no
browser trusts, and the browser's is RSA, which the printer's single ciphersuite cannot use at all.

**None of the application's own ports are published.** nginx reaches both over the compose network
and is the only thing that can, which is also what makes the client address it reports worth
believing. Routes are segregated the same way inside the app: `/p/*` exists on the printer listener
alone, and every other route exists everywhere else, so a request on the wrong one is answered `404`
— a boundary that is a socket rather than a line of proxy configuration.

> **The proxy is not optional on the printer side.** Prusa firmware can only hold very small TLS
> records, and .NET cannot produce them, so nginx terminates the printer's TLS rather than the
> application doing it. Nothing to configure — the shipped stack handles it. If you plan to
> **replace the proxy**, read [Printer TLS](docs/printer-tls.md) first: that half has requirements
> a general-purpose proxy will not meet by default, and getting them wrong breaks file transfers
> while everything else looks healthy.

> **The browser will warn on first use, and that is honest.** The certificate the proxy generates
> is signed by nobody. Serving your credentials in clear while you go and obtain a real certificate
> would be the worse default. To replace it, put `homespool.crt` and `homespool.key` into the
> `homespool-proxy-certs` volume and restart the proxy — nginx does not ask where a certificate came
> from, which is exactly why the stack ships nginx rather than something that insists on fetching
> one. `nginx/homespool.conf` has a commented HSTS line to uncomment once you have one.

> **Already run Traefik, Caddy or your own nginx?** Put it in front of the app's `8080` and point
> `XForwarded__KnownNetworks` at its network. The application is built to sit behind a proxy; the
> one it ships is a default, not a requirement — **on the people-facing side.** Keep the shipped
> proxy for the printer port, or read [Printer TLS](docs/printer-tls.md) before replacing it;
> Traefik and Caddy in particular cannot serve that port at all.

> **Do not chain another reverse proxy in front of the printer port.** One more hop that buffers
> responses or presents a certificate chain breaks transfers in ways that read as protocol bugs.
> The shipped proxy answers `404` to `/p/` on the people-facing port for the same reason.

> **Do not put that volume on NFS, CIFS or a NAS share.** SQLite's WAL locking is unreliable
> over network filesystems and will eventually corrupt the database. Use a local Docker
> volume or a bind-mount to local disk.

> **Set `PRINTER_HOST` in `.env` before the first start.** There is no way to infer your
> server's externally-reachable address from inside the container, so USB-key provisioning
> (below) won't produce a usable snippet until it's set — and the printer certificate is issued
> **once, on the first run**, covering every address the machine can see at that moment plus
> whatever `PRINTER_HOST` says. Setting it first means it is covered by construction. Setting it
> later is fine too, as long as it is one of the addresses that were detected; if it is not, delete
> `data/certificates/printer.pfx` and restart to have a new certificate issued. See
> [Configuration](#configuration).

### From source

Requires the **.NET 10 SDK** (developed against 10.0.302).

```bash
dotnet tool restore
cd Homespool.Host && dotnet libman restore && cd ..
dotnet run --project Homespool.Host
```

`dotnet libman restore` fetches the client-side assets (Bootstrap, jQuery) into
`wwwroot/lib/`, which are gitignored rather than committed. It **must be run from
`Homespool.Host/`** — LibMan resolves `libman.json` from the current directory. Rider,
Visual Studio and the Docker build do this automatically; only the CLI-from-repo-root case
needs the `cd`.

The database is created and migrated automatically on first start.

> **This serves people, not printers.** Run from source, the printer listener is plain HTTP on
> `15443` with nothing in front of it, so a printer configured with `tls = true` dials it and
> fails. No setting fixes that — see [Printer TLS](docs/printer-tls.md). For printer work from
> source you need either the Compose stack in front of it, or `PrusaConnect__PrinterTls=false`
> with printers told the same, which is the testing path described under
> [`PrinterTls`](#turning-printertls-off-and-when-that-is-legitimate). The fake printer under
> [Testing without a printer](#testing-without-a-printer) needs neither.

---

## First run

There is no open signup, and no default password. On first start — while no administrator
exists — the server prints a one-time setup token to its log:

```
No administrator account exists yet. Complete first-time setup by opening /setup and
entering this one-time token:

    <a fresh 32-character token, printed here>
```

Open `/setup`, enter that token along with the account details you want, and you have an
administrator. Until that happens every page in the web UI redirects to `/setup`, so nobody
can reach the server in the window before you have claimed it. (The printer protocol under
`/p` is exempt, so a printer still gets its documented status codes rather than a redirect
to HTML.)

The token is held **in memory only** and is regenerated on every restart — it is never
written to disk. If you miss it, restart the server and read the new one. Once an
administrator exists, `/setup` returns 404 permanently.

Everyone else joins by invitation: **Invites → Create invitation**. With SMTP configured the
invite is emailed; without it, copy the link and pass it on yourself.

---

## Adding a printer

Two ways, depending on whether the printer can already reach the server.

### USB key (works before the printer has ever connected)

Best when you are setting a printer up from scratch, or it has no way to reach the server yet.

1. **Printers → Add printer (USB key)**, give it a name and location.
2. Choose the address this printer should dial — the list is the names your printer certificate
   covers, and it defaults to `PrinterHost`.
3. **Download provisioning bundle.** This is the only time the token is available.
4. Unzip it onto the **root** of a USB stick — not into a folder, or the printer will find
   neither file — and load it from the printer's own menu: *Prusa Connect → Load Settings*.

```
prusa_printer_settings.ini     the [service::connect] section, with your token
connect.der                    the certificate authority the printer must trust
README.Bundle.md               these instructions, for whoever opens the zip
```

The README travels *in* the zip on purpose: the person unpacking it onto a stick is often not the
person who downloaded it, and by then this page is nowhere in sight.

**Nothing is transcribed, and that is the point.** Assembling this file by hand fails in ways
that look like protocol bugs: a `;` comment (this parser treats it as an error, not a comment),
an omitted key (silently reset to a default, and `token`'s default de-enrols the printer), a PEM
renamed `.der`, a mistyped code. A generated file cannot make any of them.

Only `[service::connect]` is written. Wi-Fi credentials are yours and this server neither has them
nor wants them, so the rest of the file stays your business — add your `[network]` section to the
same file or keep it in your own.

> **`custom_cert = 1` replaces the printer's trust store rather than adding to it.** While it is
> set, that printer cannot talk to Prusa Connect. The same warning is written into the ini itself,
> where it is read at the printer rather than on this page.

If you would rather read the file than trust it, *Read the ini instead* on the same page shows
exactly what it will contain.

The printer enrolls itself the moment it first connects, binding to that token. Until then
it shows as *Awaiting USB connection*, and you can reissue the token if the stick was never
written — the old one stops working immediately.

Requires `PrusaConnect:PrinterHost` to be set; the page tells you if it is not.

### Registration code (printer contacts the server first)

The flow Prusa's own firmware wizard uses: point the printer at your server, and it displays
a short code for you to claim.

#### Before the registration code works at all

A printer fresh out of the box only knows Prusa's own servers
(`buddy-a.connect.prusa3d.com`, compiled in as the default) and **there is no menu option to
change that** — the hostname field under *Settings → Network → Prusa Connect* is read-only,
not just hard to find. The only way to repoint it is loading a `prusa_printer_settings.ini`
from USB.

So the registration-code flow needs a USB step too, just a lighter one than full
provisioning — write only the `[service::connect]` host/port/tls lines, no token:

```ini
[service::connect]
hostname = printers.example.com
port = 15443
tls = true
```

Load it (**Prusa Connect → Load Settings**), *then* **Add Printer to Connect** talks to your
server instead of Prusa's, and displays a claimable code.

This is worth doing over full USB-key provisioning when the person with the USB stick isn't
the person who should claim the printer in the app — full provisioning needs `CanManage` on a
team up front; this only needs someone willing to write four lines to a stick.

#### Claiming the code

> **Ignore the QR code the printer shows.** It is hardcoded to `connect.prusa3d.com` and will
> send you to Prusa's site, not yours. The code is also shown as text underneath — that is the
> part you want.

**Printers → Claim printer**, enter the code, give it a name and location. The printer polls
until you do this, then receives its token automatically. Codes expire after **30 minutes** by
default.

Repeatedly submitting codes that match nothing backs your account off, doubling from 30
seconds up to an hour. It is the ordinary lockout shape, and it self-heals — the person
hitting it is almost always someone who mistyped, and a code expires inside the window anyway.

---

## Print files

**Files** in the navigation is a per-user store: upload a sliced model — `.gcode`, `.bgcode`,
`.gco` or `.bgc` — then rename it, delete it, or send it to a printer you have `CanUse` on.

Ownership is *where a file lives* — the store is a tree per user — rather than a permission
checked on the way past, so there is no id that hands someone else's file to whoever holds it.
Names are unique per user, which means your files are yours to name without colliding with
anyone else's.

The same store is reachable over the API by name:

```
GET    /api/v1/files                 everything you have uploaded
PUT    /api/v1/files/{fileName}      upload
GET    /api/v1/files/{fileName}      download it back
PATCH  /api/v1/files/{fileName}      rename
DELETE /api/v1/files/{fileName}      delete
```

Deliberately missing, rather than forgotten: no deduplication, no expiry — a file stays until
you delete it — and no transfer progress on the page, which would need per-file telemetry and
polling that does not exist anywhere in this app yet.

Sending a file to a printer and printing it are **separate calls**, because a file can be on a
printer without this server having put it there:

```
POST /api/v1/printers/{uuid}/files                 send one of your files to the printer
GET  /api/v1/printers/{uuid}/storage/usb/{path}    browse the printer's USB stick
POST /api/v1/printers/{uuid}/print                 print a path already on the printer
```

The send answers as soon as the printer *accepts* the transfer, not when it finishes — the
printer then pulls the bytes at its own pace, and a full-size model takes minutes. Watch
`TRANSFER_FINISHED` or the transfer fields in telemetry. Browsing the stick is gated on `CanUse`
rather than `CanRead`, since reading it means making the printer go and do work.

---

## Print queue

> **The newest part of this project.** The loop has done its job on real hardware — two prints on
> an MK3.5, the second chosen and started by Homespool after a person did nothing but set the
> printer ready. That is the thing working, on a handful of prints rather than a season of them.

Each printer has **one queue, shared by everyone** who can use it — not a queue per person. A
background loop watches the front of it and does the obvious thing: send the file, wait for the
printer to confirm it has it, start the print, move on.

`CanUse` on the printer's team lets you add, reorder and cancel entries; `CanRead` lets you watch
one. The queue is on the printer's page, on the Files page, and at:

```
GET    /api/v1/printers/{uuid}/queue                    the queue, in order
POST   /api/v1/printers/{uuid}/queue                    add a file to the back
PATCH  /api/v1/printers/{uuid}/queue/{id}               move it
DELETE /api/v1/printers/{uuid}/queue/{id}               take it out
```

**When the loop cannot proceed it holds and says why**, rather than skipping to something that
would work. That is the spooler behaviour rather than a limitation: a queue that quietly reorders
itself around an obstacle is a queue you cannot reason about.

Three of the five reasons clear themselves, and want nothing from you:

| it says | what is happening |
|---|---|
| Sending *file* to the printer | a transfer is in flight — firmware allows only one at a time |
| Waiting for the printer to confirm the file | the bytes arrived, but no `FILE_INFO` has named a path to print |
| *(nothing)* | a print has been commanded and the printer still says `READY` for a few seconds — the active print already says this, so the queue keeps quiet |

The other two are waiting for a person:

| it says | what is happening |
|---|---|
| Waiting for the printer to be made ready | the printer is not `Ready`, **most often a finished print nobody has cleared** |
| *(its own banner, with the space needed and free)* | the file at the front will not fit on the drive |

The first of those is the one to understand before you meet it, because a correctly working queue
sitting behind a finished print looks exactly like a broken one.

**Taking the print off the bed is not enough, and neither is dismissing the screen** — do that and
firmware reports `Idle`, not `Ready`. Readiness is a separate, deliberate declaration that the bed
is clear, and only a person can make it: `PUT /api/v1/printers/{uuid}/command/ready`, or *Set
Ready* at the panel. The loop will never decide this for you, because the failure mode is printing
onto a finished part, which the firmware will happily do.

**Print history** sits beside the queue on the same page. Every print is recorded, including the
ones that never started, with the file's name as it was at the time rather than a pointer to a
file that may since have been renamed or deleted.

---

## API access

`/api/v1` is this application's own API. Only `/p/*` owes Prusa's protocol anything; everything
under `/api` is shaped the way suits it.

It accepts either the sign-in cookie or a **personal access token**, so a script does not have
to reproduce the sign-in and antiforgery dance in bash. Mint one under **Account → API tokens**.
Tokens are shown **once**, at the moment they are created, and are revocable in one click from
the same page.

```bash
curl -H "Authorization: Bearer hs_..." https://homespool.example.com/api/v1/printers
```

The `hs_` prefix is deliberate: it makes a leaked token greppable, in a `.env` or a shell
history, and lets a wrong-shaped credential be rejected before anything is hashed.

An unauthenticated `/api` request is answered `401`/`403` rather than redirected to the login
page, which is the difference between a script that fails and a script that silently parses HTML.

---

## Configuration

Standard ASP.NET Core configuration: `appsettings.json`, environment variables, user secrets.
In Docker, use the `__` (double underscore) form, e.g. `PrusaConnect__PrinterHost`.

### `PrusaConnect`

| Setting | Default | Purpose |
|---|---|---|
| `PrinterHost` | *(empty)* | The hostname printers use to reach this server. **Required for USB-key provisioning** — there is no way to infer it from inside the process. |
| `PrinterPort` | `15443` | Port for the generated snippet — the host side of the printer port mapping, not the port inside the container. Not 443: that belongs to the people-facing proxy. |
| `PrinterTls` | `true` | Whether printers reach this deployment over TLS — the `tls` line in the ini **and** whether a certificate is issued for the proxy to present, so the two cannot disagree. See below. |
| `RegistrationCodeLifetimeMinutes` | `30` | How long a registration code stays claimable. Prusa's own servers use 24 h; 30 minutes is a deliberately tighter default, since the code is a credential for adopting a printer — and claiming is done standing at the printer, so the short window costs nothing real. |
| `MaxFailedClaimAttempts` | `5` | Unrecognised codes an account may submit before it is backed off. The figure Identity's own login lockout uses, for the same reason. |
| `ClaimLockoutBaseSeconds` | `30` | First backoff once that is passed. Doubles per further failure. |
| `ClaimLockoutMaxSeconds` | `3600` | Ceiling on the doubling, so the lockout always self-heals. |
| `CommandResponseTimeoutSeconds` | `10` | How long to wait for the printer's `Finished`/`Rejected`/`StateChanged` answer to a command before giving up on it. |

#### Turning `PrinterTls` off, and when that is legitimate

`PrusaConnect__PrinterTls=false` issues no certificate, so the proxy has nothing to present and stays
out of the printer path entirely. In Compose it needs the plaintext override as well, which moves the
published printer port off the proxy and onto the application:

```bash
docker compose -f compose.yaml -f compose.plaintext.yaml up
```

It exists for two jobs, both of them testing:

- **Reading the protocol on the wire.** A capture of the TLS listener is ciphertext, so packet
  capture against a real printer or the firmware rig needs the plaintext path.
- **Rigs.** `rig/enrol.sh` and `tools/slow-db/slow-db-rig.sh` drive the printer endpoints with curl
  and the fake printer; the alternative is teaching each of them to trust a CA minted minutes ago.

Every printer token then crosses the network in clear, in both directions — the one written to the
USB stick and the one issued at claim. The server logs a warning saying so at every startup. **Do not
run a deployment this way**, LAN-only included: a household LAN is exactly where a printer token is
worth taking.

### `Listeners`

One listener per credential class, so a leaked credential of one kind reaches no surface
belonging to another. Naming any of these makes Kestrel ignore `ASPNETCORE_URLS`.

| Setting | Default | Purpose |
|---|---|---|
| `PrinterPort` | `15443` | Plain HTTP: `/p/*` and nothing else. Not published to the host; the shipped proxy reaches it over the compose network and terminates the printer's TLS in front of it. Above 1024 because the container runs as a non-root user. |
| `UserPort` | `8080` | Plain HTTP: pages, `/api`, `/health` — everything except `/p/*`. Not published to the host either; same proxy, different port, different certificate. |
| `UserHttpsPort` | *(none)* | An HTTPS listener for people, using the ASP.NET development certificate or `Kestrel:Certificates:Default`. Set it only if this process should serve user TLS itself; it never carries the printer's certificate. |

### `Certificates`

The authority printers trust, minted on first run into `data/certificates` (inside the volume,
so it survives container replacement). `connect.der` is the file that goes on the USB stick.

The leaf is written twice: `printer.pfx` beside the authority, and the same certificate with its key
in PEM under `data/proxy-certificates`, which is a **separate volume** — that is the one the proxy
mounts, and it holds only the leaf. The authority's private key never goes near the container facing
the network. The PEM certificate file holds the leaf **alone with no chain appended**, which is
load-bearing: the firmware requires exactly one certificate presented, and a proxy that appends the
authority fails in a way that reads as a protocol bug.

**Its private key is the most sensitive secret in the deployment.** `custom_cert` replaces the
firmware's trust store wholesale rather than adding to it, so this CA is each provisioned
printer's *entire* trust store — and there is no revocation. Back up `data/`, but not to
somewhere you would not put a private key.

| Setting | Default | Purpose |
|---|---|---|
| `Directory` | `data/certificates` | Where the authority and leaf live. |
| `AuthorityValidityDays` | `5475` (15 years) | Replacing the authority means a USB visit to every printer, so a short life schedules guaranteed pain and mitigates nothing. |
| `LeafValidityDays` | `730` | The leaf can be replaced with a restart, because printers trust the authority rather than the leaf. |
| `AuthorityName` | `Homespool printer CA` | Cosmetic: it is never matched against anything, only read by a human inspecting `connect.der`. |

The certificate is issued **once**, at first start, and then left alone — reissuing it automatically
would drop every live printer connection each time an interface appeared, and quietly change what
this server claims to be. When this machine's addresses move, `/health` and the administrator banner
say so, and **Admin → Printer certificate** shows what the certificate covers against what the
machine now has, with a button to reissue.

A reissue needs a **server restart** to take effect, and needs nothing at any printer: they trust the
authority, which a reissue does not touch. That is the whole reason this deployment mints an
authority and a leaf rather than one self-signed certificate.

### `Smtp`

Entirely optional. With `Host` empty the server runs without outgoing mail: new accounts are
created already confirmed, invitations must be passed on by hand, and password reset is
unavailable.

| Setting | Default | Purpose |
|---|---|---|
| `Host` | *(empty)* | Mail server. Empty disables outgoing mail. |
| `Port` | `587` | 587 for STARTTLS, 465 for implicit TLS, 25 plaintext. |
| `UseImplicitTls` | `false` | `true` for implicit TLS on 465. |
| `DisableTls` | `false` | Explicit opt-in to no encryption. Never a silent fallback. |
| `UserName` / `Password` | *(empty)* | SMTP AUTH. Empty username connects without authenticating. |
| `FromAddress` / `FromName` | *(empty)* / `Homespool` | Envelope sender; falls back to `UserName`. |
| `TimeoutSeconds` | `30` | |
| `ProbeOnStartup` | `true` | Connects and authenticates once at boot purely to report a broken configuration early. Diagnostic only — it never changes behaviour. |

**Never put the SMTP password in `appsettings.json` or `compose.yaml`** — both are committed.
Use `.env` (gitignored), an environment variable, or user secrets.

### `Storage` and `Invitations`

| Setting | Default | Purpose |
|---|---|---|
| `Storage:AutoMigrate` | `true` | Apply EF migrations at startup. Safe only because exactly one process owns the database. |
| `Storage:TelemetryRetentionDays` | `14` | How long telemetry samples are kept. **`0` disables the sweep entirely.** Events are never swept. |
| `Storage:MinimumSampleIntervalSeconds` | `0` | Minimum seconds between stored samples per printer; `0` stores every message. An escape hatch, not an expectation — at 1 Hz a printer is roughly 86k rows a day, which SQLite does not mind. |
| `Storage:WriteBatchSize` | `500` | Rows buffered before the writer flushes a batch. |
| `Storage:WriteFlushIntervalSeconds` | `2` | Longest a buffered row waits before being flushed. |
| `Storage:BusyTimeoutMilliseconds` | `5000` | SQLite busy timeout. |
| `Invitations:LifetimeHours` | `48` | How long an invitation stays acceptable. |

### `PrintFiles`

Where uploaded gcode lives.

| Setting | Default | Purpose |
|---|---|---|
| `Directory` | `data/printfiles` | Relative paths resolve against the content root. Under `data/` on purpose: that is what `compose.yaml` mounts as a volume, and uploads landing anywhere else vanish when the container is replaced. |
| `MaxUploadBytes` | `536870912` (512 MiB) | Largest upload accepted. The firmware's own ceiling is 4 GiB — `orig_size` is a `uint32` on the wire — but an unbounded upload endpoint is a disk-exhaustion primitive, and a sliced model in the hundreds of megabytes is already unusual. |

The same warning as the database applies: do not put this directory on NFS, CIFS or a NAS
share. Files are read on the printer connection's own loop.

### `XForwarded`

Which proxy this deployment trusts, and what it is allowed to say about the client. The shipped
`compose.yaml` configures this for its own nginx; you need it only if you replace that proxy.

| Setting | Default | Purpose |
|---|---|---|
| `KnownProxies` | *(empty)* | Proxy addresses whose forwarded headers are honoured. |
| `KnownNetworks` | *(empty)* | Same, as CIDR ranges. |
| `ClientAddressHeader` | `X-Real-IP` | Header carrying the real client address. |
| `ForwardLimit` | `1` | How many proxies deep to trust. |

> **Configure neither and the middleware is not registered at all**, which is deliberate rather
> than merely tidy. ASP.NET performs its peer check only when at least one proxy or network is
> known; with both lists empty it skips the check and honours forwarded headers from *anyone*.
> "Trust nothing" and "trust everything" are the same configuration as far as the framework is
> concerned, so the difference has to be made by leaving the middleware out. The server warns
> when it looks like you are behind a proxy and have configured neither — the visible symptom
> otherwise is confirmation and password-reset mail that says `http://`.

---

## Development

```bash
dotnet build Homespool.slnx
dotnet test Homespool.slnx
```

In Development the OpenAPI document is served at `/openapi/v1.json` with a
[Scalar](https://scalar.com) UI at `/scalar/v1`, which is where `dotnet run` opens by default.

### Test projects

Four, split by what they need to run:

| Project | What it covers | Needs |
|---|---|---|
| `Homespool.Host.Test` | Fast, self-contained unit and service tests. | nothing |
| `Homespool.Host.E2ETest` | Drives the real ASP.NET Core pipeline via `WebApplicationFactory` — routing, authentication, middleware. | nothing |
| `Homespool.FakePrinter.Test` | The fake printer client itself — that it behaves like Buddy firmware does. | nothing |
| `Homespool.Host.IntegrationTest` | Real SMTP delivery against a live mail server. | a running Mailpit container |

The first three need nothing beyond `dotnet test`. The fourth assumes Mailpit is already
running and **will fail if it is not** — that is what makes it an integration test rather
than a unit test with a fake:

```bash
docker run -d -p 1025:1025 -p 8025:8025 axllent/mailpit
# or, for the STARTTLS tests, which need a certificate:
Homespool.Host.IntegrationTest/start-mailpit-tls.sh
```

The STARTTLS tests are the exception: they **skip** rather than fail when that script has not
been run, since the certificate it generates cannot be assumed. A clean clone therefore runs
green, and CI runs the script first so they execute there for real.

### Testing without a printer

`Homespool.FakePrinter` is a Buddy-shaped client library — it references neither `Host` nor
`Model`, so it cannot accidentally agree with the server about a wire format both got wrong.
`Homespool.FakePrinter.Cli` drives it against a genuinely running server, which is the mode that
reaches Kestrel, real TCP and real SIGTERM:

```bash
dotnet run --project Homespool.FakePrinter.Cli -- enrol
dotnet run --project Homespool.FakePrinter.Cli -- run
dotnet run --project Homespool.FakePrinter.Cli -- blast
```

`enrol` registers, prints the claim code and polls for the token; `run` behaves like a printer
until Ctrl-C; `blast` floods telemetry with no delays, for backpressure work.

`rig/` and `tools/slow-db/` drive the printer endpoints with curl for the same purpose. Both
expect the plaintext path — see `PrinterTls` above.

### Continuous integration

`.github/workflows/build-and-test.yml` runs the suite and then **builds the container images**,
which is the part a laptop cannot check for itself: a warm layer cache keeps succeeding past the
point a clean machine fails, so a broken Dockerfile can sit behind a green local build and a
green suite indefinitely.

### Database

SQLite via EF Core, one migration. The schema is regenerated in place rather than stacked
while the project is pre-release, so **a local development database may not match after a
pull** — delete `Homespool.Host/Homespool.Sqlite` and let it be recreated. Do not
do this to a database with data you care about.

---

## A Raspberry Pi image

`pi/` builds an SD-card image with the whole stack already on it — Debian trixie, arm64, Docker,
and the container images baked into the card's Docker store rather than pulled on first boot.
Flash it, boot it, browse to `http://homespool.local`.

```bash
pi/build.sh --ssh-key ~/.ssh/id_ed25519.pub
```

It needs an **Apple Silicon Mac or an arm64 Linux box** with Docker; `build.sh` refuses to run
on x86 rather than quietly spending an afternoon in qemu. [`pi/README.md`](pi/README.md) covers
how it fits together, and what a Pi 3B's radio will and will not do.

---

## License

Copyright (C) 2025-2026 Henrik O. Sørensen

[GNU Affero General Public License v3.0](LICENSE.md). If you run a modified version as a
network service, the AGPL requires you to offer that version's source to its users.

The notice lives here rather than in `LICENSE.md`, which is the licence text itself and is kept
verbatim — editing it would make it something other than the AGPL it claims to be. The AGPL's own
"how to apply these terms" asks for a copyright line and a pointer to the full notice, which is
what this section is.
