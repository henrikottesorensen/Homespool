# Homespool

A self-hosted alternative to Prusa Connect. Your models stay on your own server.

Homespool speaks the Prusa Connect protocol, so a Prusa printer can be pointed at your
own machine instead of `connect.prusa3d.com` — no cloud account, and nothing about what you
print leaves your network.

---

## Status: early, and not ready to rely on

This is a work in progress. Enrolment and authentication are built and tested; **telemetry
is received and parsed but not yet stored anywhere**, so there is no history, no dashboard,
and no statistics. If you are looking for something to actually monitor your printers with
today, this is not it yet.

**What works**

- Both printer enrolment channels (registration code, and USB-key provisioning) end to end.
- Printer authentication — fingerprint + token, over plain HTTP and the WebSocket upgrade.
- User accounts: invite-only signup, admin bootstrap, 2FA, teams, and per-team permissions.
- A WebSocket endpoint that accepts printer connections and correctly parses the telemetry
  and event stream.
- A small app-facing JSON API for listing and editing printers.

**What does not work yet**

- **Telemetry is not persisted.** Messages are parsed and logged, then discarded. The
  database tables for history and live state exist, but nothing writes to them.
- **No commands.** Nothing can be sent *to* a printer — no start/stop/pause, no file
  transfer. The command classes are empty placeholders and there is no send path.
- **No dashboard or statistics.** The web UI covers accounts, teams, invitations and a
  printer list; there is nothing that shows what a printer is doing.
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

That brings up two containers: the application, and an nginx that terminates TLS for people. The
database lives in a named volume (`printerservice-data`) so it survives container replacement.

**Everything is served over TLS from the first start, and the two audiences are kept apart.**

| | who | port | certificate |
|---|---|---|---|
| **people** | pages, `/api` | `443` (`HTTPS_PORT`), with `80` redirecting to it | self-signed on first start; replace it with your own |
| **printers** | `/p/*`, nothing else | `15443` (`PRINTER_PORT`) | minted by this deployment, and the one on the USB stick |

The application's own port is **not published**. nginx reaches it over the compose network and is
the only thing that can, which is also what makes the client address it reports worth believing.
Routes are segregated the same way inside the app: `/p/*` exists on the printer listener alone, and
every other route exists everywhere else, so a request on the wrong one is answered `404`.

> **The browser will warn on first use, and that is honest.** The certificate the proxy generates
> is signed by nobody. Serving your credentials in clear while you go and obtain a real certificate
> would be the worse default. To replace it, put `homespool.crt` and `homespool.key` into the
> `homespool-proxy-certs` volume and restart the proxy — nginx does not ask where a certificate came
> from, which is exactly why the stack ships nginx rather than something that insists on fetching
> one. `nginx/homespool.conf` has a commented HSTS line to uncomment once you have one.

> **Already run Traefik, Caddy or your own nginx?** Delete the `proxy` service, publish the app's
> `8080` yourself, and point `XForwarded__KnownNetworks` at your proxy's network. The application is
> built to sit behind one; it just no longer *requires* you to have built that yourself.

> **Do not put a reverse proxy in front of the printer port.** The printer firmware trusts a
> single anchor, requires exactly one certificate to be presented, and pushes 256 KiB transfer
> chunks that a proxy will buffer. Each of those fails in a way that looks like a protocol bug.
> The shipped proxy answers `404` to `/p/` for the same reason.

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

**Nothing is transcribed, and that is the point.** Every failure of the afternoon this was first
done by hand was an assembly failure rather than a protocol one: a `;` comment (this parser treats
it as an error, not a comment), an omitted key (silently reset to a default, and `token`'s default
de-enrols the printer), a PEM renamed `.der`, a mistyped code. A generated file cannot make any of
them.

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
until you do this, then receives its token automatically. Codes expire after an hour by
default.

---

## Configuration

Standard ASP.NET Core configuration: `appsettings.json`, environment variables, user secrets.
In Docker, use the `__` (double underscore) form, e.g. `PrusaConnect__PrinterHost`.

### `PrusaConnect`

| Setting | Default | Purpose |
|---|---|---|
| `PrinterHost` | *(empty)* | The hostname printers use to reach this server. **Required for USB-key provisioning** — there is no way to infer it from inside the process. |
| `PrinterPort` | `15443` | Port for the generated snippet — the host side of the printer port mapping, not the port inside the container. Not 443: that belongs to the people-facing proxy. |
| `PrinterTls` | `true` | Whether printers reach this server over TLS — the `tls` line in the ini **and** whether the printer listener serves TLS at all, so the two cannot disagree. See below. |
| `RegistrationCodeLifetimeMinutes` | `60` | How long a registration code stays claimable. Prusa uses 24 h; one hour is a deliberately tighter default, since the code is a credential for adopting a printer. |

#### Turning `PrinterTls` off, and when that is legitimate

`PrusaConnect__PrinterTls=false` binds the printer listener as plain HTTP and issues no certificate.
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
| `PrinterPort` | `15443` | TLS, serving the printer certificate. `/p/*` exists here and nowhere else. Above 1024 because the container runs as a non-root user. |
| `UserPort` | `8080` | Plain HTTP: pages, `/api`, `/health` — everything except `/p/*`. Not published to the host; the shipped proxy reaches it over the compose network and terminates TLS in front of it. |
| `UserHttpsPort` | *(none)* | An HTTPS listener for people, using the ASP.NET development certificate or `Kestrel:Certificates:Default`. Set it only if this process should serve user TLS itself; it never carries the printer's certificate. |

### `Certificates`

The authority printers trust, minted on first run into `data/certificates` (inside the volume,
so it survives container replacement). `connect.der` is the file that goes on the USB stick;
`printer.pfx` is what the printer listener serves.

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
| `Storage:AutoMigrate` | `true` | Apply EF migrations at startup. |
| `Storage:TelemetryRetentionDays` | `14` | Reserved — the retention sweep is not built yet. |
| `Storage:BusyTimeoutMilliseconds` | `5000` | SQLite busy timeout. |
| `Invitations:LifetimeHours` | `48` | How long an invitation stays acceptable. |

`Storage` also carries `MinimumSampleIntervalSeconds`, `WriteBatchSize` and
`WriteFlushIntervalSeconds`, declared now so the config shape does not churn later. They are
inert until telemetry persistence lands.

---

## Development

```bash
dotnet build Homespool.slnx
dotnet test Homespool.slnx
```

In Development the OpenAPI document is served at `/openapi/v1.json` with a
[Scalar](https://scalar.com) UI at `/scalar/v1`, which is where `dotnet run` opens by default.

### Test projects

Three, split by what they need to run:

| Project | What it covers | Needs |
|---|---|---|
| `Homespool.Host.Test` | Fast, self-contained unit and service tests. | nothing |
| `Homespool.Host.E2ETest` | Drives the real ASP.NET Core pipeline via `WebApplicationFactory` — routing, authentication, middleware. | nothing |
| `Homespool.Host.IntegrationTest` | Real SMTP delivery against a live mail server. | a running Mailpit container |

The first two need nothing beyond `dotnet test`. The third assumes Mailpit is already
running and **will fail if it is not** — that is what makes it an integration test rather
than a unit test with a fake:

```bash
docker run -d -p 1025:1025 -p 8025:8025 axllent/mailpit
# or, for the STARTTLS tests, which need a certificate:
Homespool.Host.IntegrationTest/start-mailpit-tls.sh
```

### Database

SQLite via EF Core, one migration. The schema is regenerated in place rather than stacked
while the project is pre-release, so **a local development database may not match after a
pull** — delete `Homespool.Host/Homespool.Sqlite` and let it be recreated. Do not
do this to a database with data you care about.

---

## License

[GNU Affero General Public License v3.0](LICENSE.md). If you run a modified version as a
network service, the AGPL requires you to offer that version's source to its users.
