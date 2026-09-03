# A Raspberry Pi image with Homespool on it

Builds an SD-card image — Debian trixie, arm64, Docker, and the Homespool compose stack with its
container images already on the card. Boot it, wait, browse to `http://homespool.local`.

```bash
pi/build.sh --ssh-key ~/.ssh/id_ed25519.pub
```

The result lands in `pi/work/out/homespool-rpi-arm64.img`. Flash **that**, not the `.img.zst` beside
it — Raspberry Pi Imager's "Use Custom" accepts a `.img` and only a `.img`, and handed a `.zst` it
writes compressed bytes to the card and then cheerfully verifies them.

## Which board it runs on

**One card, every 64-bit board.** The image is named for the architecture rather than a board because
the four v8 boards — Pi 3, Pi 4, CM4, Zero 2 W — produce a byte-identical card: only the kernel and
initramfs pair differs across `rpi-image-gen`'s device layers, and all 421 other boot files match
byte for byte.

| board | status |
|---|---|
| Pi 3B | **booted** |
| Pi 4 | **booted** |
| CM4 | untested — Pi 4 silicon, same kernel, DTB and firmware |
| Zero 2 W | untested, and the doubt is the **512 MB**, not the image: three containers plus SQLite against a measured 308 MiB idle floor |
| Pi 5 | **booted** — v8 kernel, 4K pages, ethernet and the full stack |
| CM5 | untested — Pi 5 silicon, same kernel and firmware |

**A Pi 5 runs this card.** Its firmware defaults to `kernel_2712.img` and falls back to `kernel8.img`
when that is absent, which is what this image ships; the boot partition carries every device tree
including `bcm2712-rpi-5-b.dtb`; and the v8 kernel is built with the Pi 5's silicon support
(`CONFIG_MFD_RP1`, `CONFIG_PCIE_BRCMSTB`, `CONFIG_BCM2712_IOMMU`), so the RP1 southbridge is driven
rather than merely tolerated. Confirmed on hardware — `6.18.39+rpt-rpi-v8`, 4K pages, root resized to
fill the card, ethernet up and all three containers healthy. Ethernet is the meaningful half: the
Pi 5's network MAC is inside RP1, behind PCIe.

`kernel_2712` is **not** a hardware-specific kernel. It calls itself `-v8-16k` and differs from v8 in
35 of 9857 config lines, every one of them the page size or its arithmetic.

So `--device pi5` is an optimisation, not a requirement. It builds a genuine 2712 card as
`homespool-rpi-arm64-2712.img`, buying Raspberry Pi's ~7% on random memory access and costing a 16K
ext4 block size — which wastes ~235 MiB here, because 78% of this image's files are under 16 KiB.

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
| `config/homespool.yaml` | the image definition: device layer, partition sizes, keyboard, which layers |
| `layer/homespool-rpi-all.yaml` | the device layer that puts **both** kernels on one card |
| `layer/homespool.yaml` | our layer — puts the stack at `/opt/homespool` and enables the boot unit |
| `files/homespool-firstboot.service` | what runs on the board: `setup-env.sh --no-prompt --no-overwrite`, then `compose up` |

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

**`PRINTER_HOST` is decided on the Pi, not here.** It is the address printers use and the one the
printer certificate covers, and it is minted once on first start and then frozen. An image cannot
know it — but a *booted* Pi can, because it is its own address, which is what the boot unit writes
into `.env` by running `setup-env.sh --no-prompt --no-overwrite`. The sharp edge is DHCP: if the
lease moves, the certificate stops covering the machine and wants a reissue (Admin → Printer
certificate). **Give the board a static lease** before enrolling a printer you care about.

`--no-overwrite` is why a moved lease does not quietly make things worse: the address is written on
the boot that finds `PRINTER_HOST` empty and never rewritten afterwards, so `.env` cannot drift away
from the certificate on its own. A board that genuinely moved needs the reissue either way.

The same script is how you change anything later — `cd /opt/homespool && ./setup-env.sh` over SSH
walks through the settings and leaves everything it did not ask about untouched.

**The card serves TLS with certificates it signs itself**, one per name in `USER_HOSTS`, and browsers
warn about them because nobody vouches for them. That is the honest state of an appliance on a home
network and needs no action. A board reached at a name you own in public DNS can have a certificate
browsers trust instead: the wizard asks, and `/opt/homespool/acme/` carries the installer it points
at. Nothing there runs until `sudo ./acme/install.sh` is typed, so a card with no domain is
unaffected by its presence — `acme/README.md` on the card has the rest.

**Raspberry Pi Imager's personalisation does not apply to this image.** Its hostname/user/SSH
settings rely on first-boot machinery that ships in Raspberry Pi OS and not here — verified, not
assumed: `raspberrypi-sys-mods` contains no reference to `custom.toml` at all, and only provides the
`imager_custom` helper that a Raspberry-Pi-OS-generated `firstrun.sh` calls. Set in Imager, those
settings are written and then **silently ignored**, which looks exactly like a wrong password. Use
`--ssh-key` or `--password` here instead, or set them on the card itself — see
[Getting a shell on the board](#getting-a-shell-on-the-board).

With neither, the account is locked: the stack still comes up and serves pages, but there is no shell
on the board. That is `rpi-image-gen`'s default, it is what a downloaded image ships as, and
`build.sh` warns when a build you run yourself would produce one. It is not a dead end — the card's
own `homespool-login.txt` is how a locked account gets a password or a key, before first boot or
long afterwards. See [Getting a shell on the board](#getting-a-shell-on-the-board).

## Wi-Fi, and which networks work

Put your SSID and passphrase in `homespool-wifi.txt` on the card's FAT32 partition — the only
partition a desktop machine can read — and the Pi joins on first boot. The passphrase line is blanked
once applied, because FAT32 has no file permissions and anyone who later reads the card would
otherwise find it by opening the file. That is a rewrite rather than an erase — see the note under
[Getting a shell on the board](#getting-a-shell-on-the-board), which applies here too.

**WPA3-only networks do not work on any Raspberry Pi with this image**, and WPA3 has been unreliable
on Raspberry Pi built-in wi-fi for years. **Mixed mode is the dividing line, and which side you land
on depends on the board** — established over two days on real hardware:

| your network | Pi 4 / Pi 5 | Pi 3B |
|---|---|---|
| WPA2 | works | works |
| WPA2/WPA3 mixed ("transition mode") | works — the Pi uses the WPA2 half | **will not connect** |
| WPA3-only | **will not connect** | **will not connect** |

**On a Pi 4 or Pi 5**, setting your router's network to **WPA2/WPA3 mixed** fixes it and costs nothing
for your other devices — they carry on using WPA3, only the Pi drops to WPA2. Those two boards share
the same wi-fi chipset, which is what puts them on the same side of the table.

**On a Pi 3B, mixed mode is not enough.** That board needs a network offering **WPA2 and nothing
else**, or a wired connection. Mixed mode fails there even though the Pi is only trying to use the
WPA2 half — see "Why" below. The difference is the chipset and its firmware, not the supplicant and
not anything this image does.

Either way, the alternatives are the same: plug it in, or add a second WPA2-only SSID for older
devices. Most routers can broadcast several, and the Pi is going to sit next to a printer forever.

### Why

The wi-fi chip (BCM43455 on a Pi 3B+/4/5, BCM43430 on a Pi 3B) has been sold twice since it was
designed — Broadcom to Cypress in 2016, Cypress to Infineon in 2020 — and its firmware is maintained
by a company for whom it is a minor line. WPA3's SAE handshake has never worked reliably on it.

Measured here on a Pi 4, against both a 2.4 GHz mixed AP and a 5 GHz WPA3-only one:

- the SAE exchange itself **completes**
- the association immediately afterwards is **rejected**, status 16
- identically under `iwd` and `wpa_supplicant`, on both bands

The specific fault is documented in [raspberrypi/linux#4718](https://github.com/raspberrypi/linux/issues/4718):
Raspberry Pi's driver work for SAE was done against `wpa_supplicant`, and iwd — which this image uses,
because the base OS layer provides it — drives the handshake in a way that breaks against APs
advertising H2E, which most modern ones do. The fix is an iwd patch still under review upstream.

So the image sets `SaeDisable` in `/etc/iwd/main.conf`, which tells iwd not to attempt SAE at all.
Without it you get no error worth reading — just a connection that never completes. With it, mixed
networks work immediately **on a Pi 4, and on a Pi 5** — the measurements above are from a Pi 4, and
a Pi 5 has since been confirmed to join a mixed AP too, which is what the shared chipset predicts.

### Why a Pi 3B is worse, and `SaeDisable` does not rescue it

Measured after moving a working card from a Pi 4 into a Pi 3B — same image, same credential, same
network, same 2.4 GHz channel. The Pi 3B refused it, once a minute, forever:

```
event: connect-info, ssid: example-network, bss: 00:00:5e:00:53:af, signal: -64
event: connect-failed, status: 16
```

The same board joined a plain WPA2 network immediately. So it is not the radio, the firmware load,
the regulatory domain, the credential or the band. What the failing network advertises is:

```
Authentication suites: PSK PSK/SHA-256 SAE
Capabilities: ... MFP-capable
```

That is a transition BSS: SAE *and* PSK, with management frame protection offered. **SAE was never
the obstacle here** — `SaeDisable` was already active and the client had stopped attempting it. The
BCM43430's firmware, dated 2021, cannot complete association against such a BSS at all, most likely
over `PSK/SHA-256` and PMF, neither of which a plain WPA2 network ever asks for.

Which is why the table above splits by board, and why **`status 16` should not be read as "SAE was
refused"** — it appears here with no SAE in play. It means the access point stopped answering
part-way through the handshake, and the reason has to be established each time rather than assumed.

### What would make WPA3 work

Not "nothing" — the honest answer is "not with anything a distribution ships".

Per the same issue, WPA3-Personal with CCMP does work on a 43455 given **Infineon's newer firmware
(7.45.286, released 2024-10-28)** plus a recent `wpa_supplicant`, or `iwd` with the pending patch.
That firmware is in Infineon's own release packages and is not in Debian, Raspberry Pi's archive, or
anywhere else with a maintainer.

We have not taken it. Building an appliance on a hand-fetched binary that nothing updates is a worse
problem than the one it solves, and it would have to be re-fetched by hand on every rebuild. When the
iwd patch merges and newer firmware reaches the archives, `SaeDisable` is one line to delete.

Worth knowing regardless: **WPA3 with GCMP-256 will never work on this chip.** Infineon have said the
MAC has no GCMP engine and they will not add it, and `brcmfmac` is FullMAC so there is no software
fallback. That rules out WPA3-Enterprise 192-bit permanently, which is irrelevant to a home print
server but occasionally matters to somebody.

## The root filesystem grows to fill the card

On first boot, in two halves — both of which come from `raspberrypi-sys-mods`:

1. An initramfs script grows the root **partition** to the end of the device, **before root is
   mounted**. It is gated on the word `resize` on the kernel command line, which the layer appends
   to `cmdline.txt`, and it refuses unless root is partition 2 — which it is.
2. `rpi-resize.service` then pulls in `systemd-growfs-root.service`, which grows the **filesystem**
   into the enlarged partition. `ConditionFirstBoot=yes`, so it runs once.

So `root_part_size` in the config only decides the size of the *image file*, not the size the board
ends up with. Keep it modest; the card decides.

`rpi-image-gen` deliberately provides none of this — its intended flow is `rpi-sb-provisioner` or
fastboot writing to a board you have in your hand, where you already know the size. That reasoning
does not survive contact with a downloadable image, which is the one case where you cannot know.

Done with the Raspberry Pi package rather than a `growpart` unit of our own for one reason worth
repeating: it resizes **offline, in the initramfs**. A hand-rolled version would be rewriting the
partition table of a mounted root filesystem on someone else's SD card.

## Getting a shell on the board

**There is no way to log in.** The `pi` account on a downloaded card is locked and has no authorised
key — you cannot log in, and neither can anyone who finds the board on your network. The stack runs,
the pages serve and the printers work regardless; this is only about a command line on the Pi itself.

Put a password, an SSH key, or both in **`homespool-login.txt`** on the card's FAT32 partition,
beside `homespool-wifi.txt`, and boot it. That is the whole procedure, and there is no hurry — it
works before the first boot or months later, for as long as the account still has nothing. A board
you cannot get into is always a board you can put in a laptop and give a way in.

```
password=swordfish
sshkey=ssh-ed25519 AAAAC3Nza... you@laptop
```

**Usually set both.** A key is the better credential, but it does nothing at a monitor and keyboard —
and the console is all there is on a board whose networking has broken, which is exactly when you
need to get in. A key alone can leave you at a login prompt you cannot answer.

Both lines are blanked once applied. For the password that is the point, exactly as with the wi-fi
passphrase: FAT32 has no permissions, so anyone who later reads the card would otherwise find it by
opening the file. A public key is not a secret, and is blanked only so a provisioned card settles to
an empty file — what went in is in `~/.ssh/authorized_keys` on the board.

**Blanking is a rewrite, not an erase**, and this is the limit of what it buys. The old bytes may
survive in the partition's free space, and on an SD card no software can reliably overwrite them —
the controller's wear levelling decides which flash cells a write actually lands on, so even an
in-place overwrite would be theatre. What blanking removes is the credential from the file; it does
not scrub the card. Treat a card that has ever held a password as still holding it, and if that
matters, use the file only to get in and change the password with `passwd` afterwards. The wi-fi
passphrase has exactly the same property.

**Each sets the first one and only the first.** Once the account has a password the password line
stops working; once it has an authorised key the sshkey line does. Either typed in afterwards is
refused and cleared, with the reason in `journalctl -u homespool-login`. So the file can give the
board a first way in and can never override one. Afterwards it is `passwd` and
`~/.ssh/authorized_keys` on the board.

The two gates are independent, which shows up in one case: **a card set up with only a key keeps its
password line working**, because the account still has no password. That is deliberate — it is the
console recourse if the network ever goes — and filling in both lines closes both. The same is true
of a card built with `--ssh-key` and no `--password`, where one `passwd` on the board closes it.

Nothing checks how good the password is, unlike `--password` at build time, which `rpi-image-gen`
validates against a regex wanting upper, lower, digit and punctuation. This account can be reached
over SSH from your network, so that is yours to get right.

### Why there is no stock password

There was one — `pi` / `homespool`, published right here, expired so the first login had to change
it. That is the OctoPi arrangement and it does not survive contact with a network.

Nothing in this image configures sshd, so it runs Debian's defaults: `PasswordAuthentication yes`,
`UsePAM yes`. Under PAM an expired password still **authenticates** over SSH and then prompts for a
new one — which is the same exchange this section used to describe from the owner's side, ending
their session mid-change. So between a card reaching a LAN and its owner's first login, anybody who
could reach port 22 could log in with the credential from this file and choose the new password
themselves. Expiry was the entire mitigation and it was never one: it did not gate the login, it
handed over the account and locked the owner out.

The recovery argument that put it there — a board that will not join the network is also a board you
cannot log into to find out why, and the only way in was editing `cmdline.txt` for an `init=/bin/sh`
shell — is answered better by the file above. Same partition, same physical operation as setting the
wi-fi, and it stays available for exactly as long as the board is unreachable.

## First boot

Quick, now that the container store ships populated — the unit brings the stack up and the app
migrates its database, and that is all. The systemd unit still allows 30 minutes, because the
migration on a Pi 3 is the slow part and a timeout mid-migration is worse than waiting.
`homespool.local` answering is the signal it finished; `journalctl -u homespool-firstboot` is where
it says why not.
