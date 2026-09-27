# Checking for newer images

**Optional, and it only reports.** Once a day this looks at the Homespool containers running on the
machine, asks their registry whether it now publishes a newer image under the same tag, and says
what pulling it would bring. It never pulls, restarts or changes anything; updating stays a thing you
do.

## Whether you need it

| your situation | what to do |
|---|---|
| You build the images yourself with `./build.sh` | **Nothing.** They name no registry, so there is nothing published to compare with — the check says so and stops. |
| You pull published images (`REGISTRY` set in `.env`) | Use this. |
| A Raspberry Pi card | **Already installed**, and it runs from the first day. |

The registry has to allow **anonymous reads** of the three images. GHCR's public packages do. The
check asks with no login on purpose: it should need nobody's credentials to find out whether
something newer exists.

## What it reports

For the application, the proxy and the camera sidecar, one of:

| status | meaning |
|---|---|
| `current` | The running image is the one the registry serves under that tag. |
| `newer` | The registry serves a different one. The report says what it would bring. |
| `local` | Built from source rather than pulled — where the stack runs, or on the machine that made the card — so nothing is published to compare with. Reported with the date it was built. |
| `pinned` | Started from a digest, not a tag, so there is nothing to follow. |
| `not-running` | No such container in this compose project. |

For a newer image, what it would bring:

- **Homespool fixes** — the commits between the revision running here and the published one, counted
  by type. Found in the published revision's history, so this machine's own revision is never sent
  anywhere.
- **.NET runtime releases** in between, marked when they were security releases.
- **A rebuild or a newer base** — the same revision rebuilt on newer packages, or built on a newer
  base image; published for what it fixes underneath.

It lands in the journal, a line per container, and as JSON in `/var/lib/homespool/update-check.json`:

```bash
journalctl -u homespool-update-check.service
```

**The application shows it too.** The check copies the report into the `homespool-update-report`
volume, which `compose.yaml` mounts read-only into the application, and its health report says what
the check found, a line per container. A newer image with a reason to take it — a Homespool fix, a
.NET security release, a rebuild — puts it in the administrators' banner, with the command to pull
it. So does a report older than three days, because then the check has stopped. Every other status is
said as what it is and stays off the banner; `local` above all is never called current. The
application never checks for updates itself; with no check installed it says nothing at all. Only
administrators see any of this: the check is left out of the status anonymous `/health` answers,
because "this deployment is behind a published fix" is not for whoever happens to ask.

## Who runs what

Three steps of one service, because only two of them need root, and those two touch nothing from
outside:

| step | runs as | does |
|---|---|---|
| `collect` | root | Reads which containers run which images from the Docker socket. No network. |
| `compare` | `homespool-update`, sandboxed | Asks the registry, GitHub and Microsoft, and parses every answer. No Docker socket, no capabilities, a read-only system. |
| `publish` | root | Copies the report into the application's volume, after checking it: no symlink in its place, no larger than a megabyte, and the shape of a report — parsed as `homespool-update`, not as root. |

`homespool-update` is created by `install.sh` from `homespool-update-check.sysusers`, and belongs to
no group but its own — above all not `docker`.

If the registry, GitHub or Microsoft's release metadata cannot be reached, the run fails and the
previous report stays where it was. A report built from part of the evidence would say "nothing new"
about the part it could not see.

## Install

On a machine that already runs the stack:

```bash
sudo ./update-check/install.sh
```

The argument, when the stack is not in `/opt/homespool`, is its compose project name — the
deployment directory's name unless `.env` sets `COMPOSE_PROJECT_NAME`. It needs `docker` with the
buildx plugin, `curl`, `jq`, `setpriv` and `systemd-sysusers`, and refuses to install without them.

To check straight away rather than tonight:

```bash
sudo systemctl start homespool-update-check.service
```

## Removing it

```bash
sudo systemctl disable --now homespool-update-check.timer
sudo rm /etc/systemd/system/homespool-update-check.{service,timer} /usr/local/sbin/homespool-update-check
sudo rm /usr/lib/sysusers.d/homespool-update-check.conf && sudo userdel homespool-update
sudo systemctl daemon-reload
```

And the application's copy, or three days later its banner reports the check as stopped — which, by
then, it is. With the stack in `/opt/homespool`:

```bash
sudo rm "$(docker volume inspect --format '{{.Mountpoint}}' homespool_homespool-update-report)/update-check.json"
```
