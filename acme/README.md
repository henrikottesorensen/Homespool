# Automatic certificates, for a name the internet can verify

**Optional.** Homespool serves TLS out of the box: the proxy mints a self-signed certificate for
every name in `USER_HOSTS` on first start. That is a complete, working deployment. Browsers warn
that the certificate is signed by nobody, because it is.

This directory removes that warning — for names a public certificate authority can verify.

## Whether you need it

| your situation | what to do |
|---|---|
| Reached only at `homespool.lan`, `.local`, or a bare IP address | **Nothing.** No authority can verify those names; self-signed is the correct answer and always will be. |
| You own a domain and point a name at this machine | Use this. |
| Both — a public name *and* a LAN name | Use this, and name **only the public one** in `ACME_HOSTS`. Each name gets the best certificate available to it. |

That last row is the normal case and it is why certificates are per name. One certificate covering
every name cannot exist here: no authority will sign `homespool.lan`.

## What it does

Runs [lego](https://go-acme.github.io/lego/) daily in a container, answering the ACME **DNS-01**
challenge. The challenge is answered by writing a DNS record, so **nothing has to be reachable from
the internet** — no port 80 forward, no public address. A machine behind a household router can hold
a certificate for a name that resolves anywhere.

lego writes into `certificates/` inside the certificate volume, which is the first place the proxy
looks. There is nothing to link, copy or rename; a certificate obtained is a certificate served, at
the next proxy restart.

## Setting it up

```bash
sudo ./acme/install.sh /opt/homespool
```

Then three things, in any order:

**1. Provider credentials** in `/etc/lego/dns.env` (created empty, `0600`, outside the checkout —
this repository is public). Ask lego what your provider wants:

```bash
sudo docker compose --profile certs run --rm certs dnshelp -c cloudflare
```

Cloudflare wants a token scoped to `Zone:DNS:Edit` on the single zone. `CF_DNS_API_TOKEN` is the
canonical name and `CLOUDFLARE_DNS_API_TOKEN` an accepted alias:

```
CF_DNS_API_TOKEN=...
```

> **`sudo` on any `--profile certs` command.** Compose reads `env_file` itself, as whoever typed the
> command — not in the container — so a `0600` root-owned credentials file makes those commands
> root-only. Without `sudo` they fail with a bare `permission denied` naming the file. Renewal is
> unaffected, because the timers run as root, and an ordinary `docker compose up` is unaffected too:
> compose reads an `env_file` only for services in an active profile.

**2. `.env`**, which `setup-env.sh` will ask about:

```
ACME_HOSTS=homespool.example.com
ACME_EMAIL=you@example.com
ACME_DNS_PROVIDER=cloudflare
```

Every name in `ACME_HOSTS` must also be in `USER_HOSTS`, or nothing serves what is obtained for it.

**3. Run it once** rather than waiting for the timer:

```bash
sudo systemctl start homespool-renew-cert.service
journalctl -u homespool-renew-cert.service -n 50
```

## Other providers

lego supports around a hundred. Set `ACME_DNS_PROVIDER` to its name and put whatever
`dnshelp -c <provider>` lists into `/etc/lego/dns.env`. Nothing in `compose.yaml` changes — the
credentials file is passed through whole, so a provider this project has never heard of works the
same way.

Any lego setting can go in that file, since every flag has an environment variable.
`LEGO_DNS_RESOLVERS=1.1.1.1:53` is the one worth knowing: lego uses the system resolver by default,
which is wrong on a network whose DNS answers differently from the public internet.

## When it goes wrong

Two timers. `homespool-renew-cert` does the work; `homespool-check-cert` exists because **every way
the renewal can fail is quiet** — a revoked credential, a renamed provider variable, a timer nobody
enabled. None of them produce a symptom until the certificate expires and every browser refuses the
site at once. The check warns 21 days ahead and fails its unit, so `systemctl --failed` reports it.

```bash
systemctl list-timers 'homespool-*'
journalctl -u homespool-renew-cert.service -n 50
sudo systemctl start homespool-check-cert.service   # run the check now
```

**A name still showing a browser warning** is the common one, and it usually means the certificate
was obtained but is not being served. Check that the name is in `USER_HOSTS` as well as
`ACME_HOSTS`, then check what the proxy actually chose:

```bash
docker compose logs proxy | grep 26-user-tls-servers
```

It names the certificate file it picked for each name. `certificates/<name>.crt` is the issued one;
a bare `<name>.crt` is the self-signed one.

## Turning it off

```bash
sudo systemctl disable --now homespool-renew-cert.timer homespool-check-cert.timer
```

Clear `ACME_HOSTS` in `.env` and restart the proxy. Existing certificates stay in the volume and go
on being served until they expire; delete `certificates/` inside the volume to fall back to
self-signed immediately.
