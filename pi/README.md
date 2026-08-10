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

**`PRINTER_HOST` is decided on the Pi, not here.** It is the address printers use and the one the
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

## Wi-Fi, and why WPA3 does not work

Put your SSID and passphrase in `homespool-wifi.txt` on the card's FAT32 partition — the only
partition a desktop machine can read — and the Pi joins on first boot. The passphrase line is blanked
once applied, because FAT32 has no file permissions and anyone who later reads the card would
otherwise have your wi-fi password.

**WPA3 does not work with the firmware and software this image ships**, and has been unreliable on
Raspberry Pi built-in wi-fi for years. **How much it costs you depends on the board**, which took two
days on real hardware to establish:

| your network | Pi 4 | Pi 3B |
|---|---|---|
| WPA2 | works | works |
| WPA2/WPA3 mixed ("transition mode") | works — the Pi uses the WPA2 half | **will not connect** |
| WPA3-only | **will not connect** | **will not connect** |

**On a Pi 4**, setting your router's network to **WPA2/WPA3 mixed** fixes it and costs nothing for
your other devices — they carry on using WPA3, only the Pi drops to WPA2.

**On a Pi 3B, mixed mode is not enough.** That board needs a network offering **WPA2 and nothing
else**, or a wired connection. Mixed mode fails there even though the Pi is only trying to use the
WPA2 half — see "Why" below.

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
networks work immediately **on a Pi 4**.

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
