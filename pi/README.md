# A Raspberry Pi image with Homespool on it

Builds an SD-card image — Debian trixie, arm64, Docker, and the Homespool compose stack with its
container images already on the card. Boot it, wait, browse to `http://homespool.local`.

```bash
pi/build.sh --ssh-key ~/.ssh/id_ed25519.pub
```

The result lands in `pi/work/out/homespool-pi3.img.zst`. Flash it with Raspberry Pi Imager
("Use Custom"), or decompress it and `dd` it yourself.

## What you need

An **Apple Silicon Mac or an arm64 Linux box**, with Docker. Both halves of this build target arm64 —
the application images and the card's root filesystem — so on an arm64 host nothing is emulated and
the whole thing takes minutes. `build.sh` refuses to run on x86 rather than quietly spending an
afternoon in qemu.

## How it fits together

| file | what it is |
|---|---|
| `build.sh` | the entry point: builds the app images, stages the payload, drives the image build |
| `Dockerfile.builder` | Debian trixie + `rpi-image-gen`, pinned to a SHA. Its supported host is Debian arm64, which macOS is not — hence a container |
| `config/homespool-pi3.yaml` | the image definition: board, partition sizes, which layers |
| `layer/homespool.yaml` | our layer — puts the stack at `/opt/homespool` and enables the boot unit |
| `files/homespool-firstboot.*` | what runs on the board: load images, write `.env`, `compose up` |

The container images are **baked into the card's Docker store**, not pulled and not loaded on the
board. `build.sh` splits the image build in two (`-f`, then `-i`) and, in between, points a real
`dockerd` at the half-built root filesystem's `/var/lib/docker` to load them there.

That split is worth the trouble. Doing it with `docker load` on first boot would make every install
repeat ~550 MB of layer unpacking and minutes of Pi 3 CPU — on an SD card — to arrive at a state
byte-identical on every card. Doing it here costs one sequential write at flash time instead. It
also has to be a real daemon: `/var/lib/docker` is not a directory you can assemble by copying,
because overlay2's layer tree and the content store are daemon-managed.

Nothing is published to a registry yet; when there is a tagged release the `docker save` becomes a
`docker pull` and nothing else changes.

## The two things that surprise people

**`PRINTER_HOST` is decided on the Pi, not here.** It is the address printers dial and the one the
printer certificate covers, and it is minted once on first start and then frozen. An image cannot
know it — but a *booted* Pi can, because it is its own address, which is what
`homespool-firstboot.sh` writes into `.env`. The sharp edge is DHCP: if the lease moves, the
certificate stops covering the machine and wants a reissue (Admin → Printer certificate). **Give the
board a static lease** before enrolling a printer you care about.

**Raspberry Pi Imager's personalisation does not apply to this image.** Its hostname/user/SSH
settings rely on first-boot machinery that ships in Raspberry Pi OS and not here — verified, not
assumed: `raspberrypi-sys-mods` contains no reference to `custom.toml` at all, and only provides the
`imager_custom` helper that a Raspberry-Pi-OS-generated `firstrun.sh` calls. Set in Imager, those
settings are written and then **silently ignored**, which looks exactly like a wrong password. Use
`--ssh-key` or `--password` here instead.

With neither, the account is locked: the stack still comes up and serves pages, but there is no way
in. That is `rpi-image-gen`'s default and `build.sh` warns about it.

## The root filesystem does not grow

Whatever `root_part_size` produced is what the board lives with, on a 16 GB card or a 256 GB one.
`raspberrypi-sys-mods` is not installed, so there is no `init_resize` and no `resize_early`; `fstab`
is unconfigured, so `systemd-growfs-root` never fires; and the table is MBR, so GPT auto-discovery
does not apply. Nothing here is broken — it is simply absent, and it is the first thing to add if
these images ever go to anyone else.

## First login

`pi` / `homespool`, and the password is expired — so the first login asks for it again and then for a
new one twice. **Over SSH, changing it ends the session**; that is sshd completing the PAM exchange
and closing, not a failure. Reconnect with the new password.

Worth knowing before you do anything clever: a session that drops at exactly that moment leaves you
typing into your *own* machine. `sudo iwctl` on a Mac reports `command not found`, which reads
disturbingly like a corrupted card.

## First boot

Quick, now that the container store ships populated — the unit brings the stack up and the app
migrates its database, and that is all. The systemd unit still allows 30 minutes, because the
migration on a Pi 3 is the slow part and a timeout mid-migration is worse than waiting.
`homespool.local` answering is the signal it finished; `journalctl -u homespool-firstboot` is where
it says why not.
