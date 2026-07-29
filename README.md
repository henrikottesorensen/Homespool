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
cp .env.example .env      # set at least PUBLIC_HOST before printers need to reach this server
docker compose up --build
```

The database lives in a named volume (`printerservice-data`) so it survives container
replacement. The container publishes port `8080` by default (`PORT` in `.env` to change it).

> **Do not put that volume on NFS, CIFS or a NAS share.** SQLite's WAL locking is unreliable
> over network filesystems and will eventually corrupt the database. Use a local Docker
> volume or a bind-mount to local disk.

> **Set `PUBLIC_HOST` in `.env` before adding printers.** There is no way to infer your
> server's externally-reachable address from inside the container, so USB-key provisioning
> (below) won't produce a usable snippet until it's set. If a reverse proxy terminates TLS in
> front of this container, `PUBLIC_HOST`/`PUBLIC_PORT`/`PUBLIC_TLS` describe the proxy's
> address, not the container's. See [Configuration](#configuration).

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
2. Copy the `[service::connect]` snippet it shows you — this is the only time the token is
   displayed.
3. Paste it into `prusa_printer_settings.ini` on the printer's USB stick, alongside your own
   `[network]` and Wi-Fi settings, and insert the stick.

```ini
[service::connect]
hostname = printers.example.com
port = 443
tls = True
token = <generated for you>
```

The snippet only ever covers `[service::connect]`. Wi-Fi credentials are yours and this
server neither has them nor wants them, so the rest of the file stays your business.

The printer enrolls itself the moment it first connects, binding to that token. Until then
it shows as *Awaiting USB connection*, and you can reissue the token if the stick was never
written — the old one stops working immediately.

Requires `PrusaConnect:PublicHost` to be set; the page tells you if it is not.

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
port = 443
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
In Docker, use the `__` (double underscore) form, e.g. `PrusaConnect__PublicHost`.

### `PrusaConnect`

| Setting | Default | Purpose |
|---|---|---|
| `PublicHost` | *(empty)* | The hostname printers use to reach this server. **Required for USB-key provisioning** — there is no way to infer it from inside the process. |
| `PublicPort` | `443` | Port for the generated snippet. |
| `PublicTls` | `true` | Whether printers should use TLS. |
| `RegistrationCodeLifetimeMinutes` | `60` | How long a registration code stays claimable. Prusa uses 24 h; one hour is a deliberately tighter default, since the code is a credential for adopting a printer. |

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
