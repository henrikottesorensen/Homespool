#!/usr/bin/env bash
# Builds a Raspberry Pi SD-card image with the Homespool stack baked in.
#
#   pi/build.sh --ssh-key ~/.ssh/id_ed25519.pub
#   pi/build.sh --ssh-key ~/.ssh/id_ed25519.pub --device pi5 --dev
#
# The artefact is homespool-rpi-arm64.img, one card carrying both kernels so any 64-bit Pi boots it -
# see the --device block below. --dev adds the .NET SDK, Claude Code and debugging tools on top of
# the appliance and suffixes the name; it wants a Pi 4 or better.
#
# Two builds happen here, and the order matters. First the application's own container images, built
# natively for arm64 on this machine - which is why an Apple Silicon Mac is the easy host and an x86
# one is not. Then the SD-card image, assembled by rpi-image-gen inside a privileged Debian container
# because its supported host is Debian arm64 and macOS is not that.
#
# The container images are baked into the card rather than pulled. Nothing is published to a registry
# yet; when there is a tagged release, the docker save below becomes a docker pull and nothing else
# in here changes.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pi_dir="$repo_root/pi"
work_dir="$pi_dir/work"
payload_dir="$work_dir/payload"
images_dir="$work_dir/images"

ssh_key=""
password=""
device_user="pi"

# The board, and it decides only the kernel flavour. rpi-image-gen ships pi3, pi4, cm4 and zero2w on
# a shared rpi-generic64 base - the v8 kernel, 4K pages - while pi5 and cm5 require rpi-linux-2712
# instead, which is 16K pages and therefore a 16K ext4 block size derived from them.
#
# The four v8 boards produce a byte-identical card, verified 2026-08-11 rather than assumed: diffing
# a pi4 build against a pi5 one found only the kernel and initramfs pair differing by name, with all
# 421 shared boot files matching byte for byte. So they share one artefact rather than being four
# identically-shaped copies under four names.
#
# A v8 card also runs a Pi 5, verified on hardware 2026-08-11: the firmware falls back to kernel8.img
# when kernel_2712.img is absent, and the board came up to `uname -r` = 6.18.39+rpt-rpi-v8 with
# 4K pages, ethernet, SSH and the whole stack healthy. Ethernet is the part that matters, since the
# Pi 5's MAC lives inside RP1 behind PCIe - there is no reaching that shell without the southbridge.
#
# Which makes 2712 an optimisation rather than a requirement: it is not a hardware-specific kernel,
# calls itself -v8-16k, and differs from v8 in 35 of 9857 config lines, every one the page size or
# its arithmetic. What it buys is Raspberry Pi's ~7% on random memory access; what it costs is a 16K
# ext4 block size, which wastes ~235 MiB here because 78% of this tree's files are under 16 KiB.
#
# So the default is "all", which is neither of those two cards but a third carrying *both* kernels -
# layer/homespool-rpi-all.yaml, and the firmware picks per board with no configuration involved. That
# is strictly better than choosing for people: an older board gets v8, a Pi 5 gets its 16K pages and
# the 7%, and nobody has to know which card they need.
#
# The single-board targets stay for the two cases that still want them - measuring one kernel against
# the other, and building the smallest card for a board you have in your hand. Each keeps a distinct
# artefact name so three differently-shaped cards cannot overwrite each other in work/out.
board="all"

# The appliance, or the appliance plus a toolchain. --dev swaps the custom layer for homespool-dev,
# which requires homespool rather than replacing it, so a dev card is this same image with the .NET
# SDK, Claude Code and debugging tools on top. Wants a Pi 4 or better - see that layer's header.
custom_layer="homespool"

while [ $# -gt 0 ]; do
    case "$1" in
        --ssh-key)   ssh_key="$2"; shift 2 ;;
        --password)  password="$2"; shift 2 ;;
        --user)      device_user="$2"; shift 2 ;;
        --device)    board="$2"; shift 2 ;;
        --dev)       custom_layer="homespool-dev"; shift ;;
        # 2,9 rather than 2,12: the usage block ends at the --dev note, and a fixed range that runs
        # past it prints half a sentence about container builds. Extend this when the header does.
        -h|--help)   sed -n '2,9p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

# Spelled out rather than derived, because rpi-image-gen's layer names do not follow from its
# directory names and there is no rule to infer: device/pi4 declares "rpi4", but device/cm4 declares
# "rpi-cm4" and device/zero2w declares "rpizero2w". Guessing "r$board" works for three of the six and
# fails the rest several minutes into a build, saying only that a layer was not found.
case "$board" in
    all)    device_layer=homespool-rpi-all ;;
    pi3)    device_layer=rpi3 ;;
    pi4)    device_layer=rpi4 ;;
    pi5)    device_layer=rpi5 ;;
    cm4)    device_layer=rpi-cm4 ;;
    cm5)    device_layer=rpi-cm5 ;;
    zero2w) device_layer=rpizero2w ;;
    *) echo "unknown --device: $board (all, pi3, pi4, pi5, cm4, cm5, zero2w)" >&2; exit 2 ;;
esac

# Named for the kernel flavour rather than the board, because that is the only thing that varies. The
# v8 boards deliberately share one name - they are one artefact, and four names for it invite picking
# "the pi3 one" for a Pi 3 as though the others would not do. Pi 5 and CM5 genuinely differ, so they
# get their own and cannot silently overwrite the v8 card in work/out.
case "$board" in
    all)     image_name="homespool-rpi-arm64" ;;
    pi5|cm5) image_name="homespool-rpi-arm64-2712" ;;
    *)       image_name="homespool-rpi-arm64-v8" ;;
esac
if [ "$custom_layer" = "homespool-dev" ]; then
    # An if rather than a one-line [ ... ] && ..., which under set -e is a trap: the test returns 1
    # on an appliance build and takes the whole script down with it.
    image_name="$image_name-dev"
fi

# Checked here rather than where it is used, which is on the far side of a ~550 MB image save and a
# container build. A typo in a path should cost a second, not the whole build.
if [ -n "$ssh_key" ] && [ ! -f "$ssh_key" ]; then
    echo "No such public key: $ssh_key" >&2
    exit 2
fi

# The layer metadata between METABEGIN and METAEND is DEB822, not free-form comment. A prose line
# that is not indented as a continuation makes the *whole layer* fail to parse, and the build then
# says "Layer 'homespool' not found" several minutes in - naming neither the file nor the line, and
# reading like a missing file rather than a punctuation error. It cost two build cycles to learn.
#
# The key pattern allows underscores, and that was a bug here until 2026-08-11: written as
# [A-Za-z0-9-]* it rejected X-Env-Var-storage_type, which is rpi-image-gen's *own* spelling in every
# device layer it ships. Our layers had simply never used an underscore, so a lint that would refuse
# valid upstream metadata sat here looking correct - and it failed the first layer that followed the
# tool's conventions rather than ours, reporting five good lines as malformed.
for layer in "$pi_dir"/layer/*.yaml; do
    bad=$(awk '/^# METABEGIN/{f=1;next} /^# METAEND/{f=0} f{
              if ($0 ~ /^#$/ || $0 ~ /^#  / || $0 ~ /^# [A-Za-z][A-Za-z0-9_-]*:/) next
              printf "  line %d: %s\n", NR, $0
           }' "$layer")
    if [ -n "$bad" ]; then
        echo "$(basename "$layer"): malformed metadata - continuation lines must start '#  '" >&2
        echo "$bad" >&2
        exit 2
    fi
done

# An x86 host would need every arm64 package and both application images built under qemu. That is
# not a warning worth burying - it is the difference between a five minute build and an afternoon.
if [ "$(uname -m)" != "arm64" ] && [ "$(uname -m)" != "aarch64" ]; then
    echo "This host is $(uname -m). Both builds target arm64 and would run emulated." >&2
    echo "Continue only if you know you want that." >&2
    exit 1
fi

# There is no stock password any more, and this is where one used to be.
#
# It was "homespool", the OctoPi arrangement, adopted because rpi-image-gen's own default is to
# *lock* the account - correct for a fleet, and useless for an appliance whose board would not join
# the network, since that is then also a board you cannot log into to find out why. The only way in
# was editing cmdline.txt for an init=/bin/sh shell, which happened here, and settled it.
#
# What settled it the other way is that a published password is a remote one. Nothing in this image
# configures sshd, so it runs Debian's PasswordAuthentication yes with UsePAM yes - and under PAM an
# expired password still authenticates over SSH and then prompts for a new one. Expiry was the whole
# mitigation, and it was not one: between a card reaching a LAN and its owner's first login, anybody
# who could reach port 22 could log in with the credential from the README and set a password of
# their own, locking the owner out of their own board.
#
# The recovery case that argued for it is answered better by /boot/firmware/homespool-login.txt,
# which supplies the first password and only the first. It is the same physical operation as setting
# the wi-fi, on the same partition, and it stays available for as long as the account has no
# password - so a board nobody can get into can always be given a way in, which is more than the
# init=/bin/sh hack ever offered.

# ------------------------------------------------------------------------------------------------
# 1. The application images, native arm64.
# ------------------------------------------------------------------------------------------------
echo "==> Building the Homespool container images for arm64"
# Stamp the commit into the images. It matters more on a card than anywhere else: this artefact gets
# written to an SD card, posted, and run for months by somebody who never built it, and "which build
# is this?" then has exactly one answer - `docker compose run --rm homespool --version`. Compose
# cannot compute it (no command substitution), so it is computed here; see tools/gitref.sh.
HOMESPOOL_GITREF="$("$repo_root/tools/gitref.sh")"
export HOMESPOOL_GITREF
docker --log-level warn compose -f "$repo_root/compose.yaml" build

# ------------------------------------------------------------------------------------------------
# 2. The payload: everything the card needs at /opt/homespool.
#
# Rebuilt from scratch each run. It is derived entirely from the working tree, so a stale payload is
# only ever a way to ship something nobody can reproduce.
# ------------------------------------------------------------------------------------------------
echo "==> Staging the payload"
rm -rf "$payload_dir"
mkdir -p "$payload_dir/nginx"

cp "$repo_root/compose.yaml" "$payload_dir/"
cp "$repo_root/.env.example"  "$payload_dir/"
# The board configures itself with this on first boot, and an operator can re-run it over SSH to add
# SMTP or repoint PRINTER_HOST later. Mode carried explicitly: systemd ExecStart needs it executable,
# and cp -a into the image preserves whatever arrives here.
install -m 0755 "$repo_root/setup-env.sh" "$payload_dir/setup-env.sh"
# The whole directory, deliberately. A hand-list of just the files compose bind-mounts drifted when
# two mounts were added without it noticing - and a missing bind source is not a merely absent
# config: Docker on the card invents the path as an empty directory, nginx hits it through an
# include, and the proxy never starts. The directory is also compose.yaml's build context for the
# proxy, so shipping it whole
# keeps the card's compose project self-consistent. It is 64 KB against ~550 MB of images.
cp -R "$repo_root/nginx/." "$payload_dir/nginx/"

# Automatic certificates, which the card does NOT use unless somebody asks for it: nothing here runs
# until `sudo ./acme/install.sh` is typed, and a card with no domain never has cause to. It ships
# anyway because setup-env.sh ships, and the wizard's answer to "do you have a public domain name"
# ends by telling the operator to run exactly that path - which is a dead end on a card that does not
# carry it, and a card owner is the least likely person to have a checkout to fall back on.
#
# The whole directory, for the reason the nginx copy above gives: a hand-list drifts.
mkdir -p "$payload_dir/acme"
cp -R "$repo_root/acme/." "$payload_dir/acme/"

# Modes carried explicitly, as with setup-env.sh: `cp -R` preserves whatever arrives here, and a
# checkout that did not keep the executable bit would ship an installer nobody can run - reported as
# "permission denied" against a script the operator was just told to type.
chmod 0755 "$payload_dir/acme"/*.sh

# Deliberately NOT into the payload. This tarball never reaches the card: it is loaded into the
# card's Docker store during the build (step 4), so the Pi boots with the images already unpacked.
# Shipping it as well would put ~200 MB on the card that exists only to be expanded and deleted.
echo "==> Saving the container images (the slow part, ~550 MB uncompressed)"
mkdir -p "$images_dir"

# The same REGISTRY prefix compose.yaml applies, read from .env the way compose reads it - otherwise
# setting it would tag the built images one way and have this look for them the other, and the only
# symptom would be `docker save` failing on an image nobody can see is missing. The card needs no
# registry at run time either way: whatever these are called, they are already in its store, and its
# compose.yaml asks for the same names because it expands the same variable.
registry="${REGISTRY:-$(sed -n 's/^REGISTRY=//p' "$repo_root/.env" 2>/dev/null | tail -1)}"
image_prefix="${registry:+${registry}/}"

docker save "${image_prefix}homespool:latest" "${image_prefix}homespool-proxy:latest" \
    | gzip -1 > "$images_dir/homespool-images.tar.gz"
echo "    $(du -h "$images_dir/homespool-images.tar.gz" | cut -f1) saved"

# ------------------------------------------------------------------------------------------------
# 3. The builder.
# ------------------------------------------------------------------------------------------------
echo "==> Building the image-builder container"
docker build --quiet -t homespool-imagegen -f "$pi_dir/Dockerfile.builder" "$pi_dir" >/dev/null

# Spelled with the full IGconf_ prefix. The bare form is accepted as a key=value pair and then
# matches no declared variable, so the build fails several stages later saying the variable was
# never set - which reads like a bug in the layer rather than in this list.
overrides=(
    "IGconf_homespool_payload=/repo/pi/work/payload"
    "IGconf_homespool_assetdir=/repo/pi/files"
    "IGconf_device_user1=$device_user"
    "IGconf_device_layer=$device_layer"
    "IGconf_image_name=$image_name"
    "IGconf_layer_custom=$custom_layer"

    # Sudo for the device user, stated rather than inherited - and the inheritance is what went
    # wrong. rpi-image-gen defaults user1sudo to "none" and then *lazily* rewrites it to "passwd"
    # when a build-time password is set. So while this image shipped a stock password the account
    # quietly gained sudo as a side effect, and dropping that password quietly took it away: every
    # card built since has an empty sudo group, no sudoers.d grant and a locked root. Nothing said
    # so, and nobody noticed, because the docker group is root-equivalent and covers the same ground.
    #
    # The knob is not the problem - none|passwd|nopasswd is the documented interface. Being written
    # by a trigger on an unrelated variable is. Setting it here is the same move as spelling out
    # fs_ext4_mkfs_args rather than letting page_size derive it.
    #
    # passwd rather than nopasswd: nopasswd hands root to whoever holds the SSH key with nothing
    # further asked. passwd wants the owner's own password, which does not exist until they choose
    # one - and a locked account cannot sudo at all. Verified rather than assumed: locked, sudo
    # answers "1 incorrect password attempt"; after chpasswd sets a password the same command
    # returns uid=0. So the grant lies dormant until somebody sets a credential through
    # homespool-login.txt, which is the first-boot story this image is built around.
    "IGconf_device_user1sudo=passwd"
)

# The combined card must be built at a 4K ext4 block size, and getting this wrong produces a card
# that boots a Pi 5 perfectly and cannot mount root on anything older - which is the worst shape of
# bug available here, since the build is silent and only half the hardware shows it.
#
# The mechanism: rpi-linux-2712 declares page_size 16384 with policy *force*, and image-rpios lazily
# derives fs_ext4_mkfs_args as "-F -b ${IGconf_linux_page_size}". Requiring both kernels therefore
# inherits the force and mkfs runs with -b 16384. ext4 refuses to mount a filesystem whose block size
# exceeds the mounting kernel's page size, so every 4K-page board - pi3, pi4, cm4, zero2w - dies at
# root mount. Observed directly, not reasoned: loop-mounting a 2712-built image on a 4K-page kernel
# gives "EXT4-fs: bad block size 16384".
#
# So the derived variable is overridden rather than page_size, which cannot be - force wins over an
# override. That leaves the Pi 5 still running 16K MMU pages, since the kernel's granule is compiled
# in and no IGconf variable rebuilds it; only the filesystem geometry changes.
if [ "$board" = "all" ]; then
    overrides+=("IGconf_fs_ext4_mkfs_args=-F -b 4096")
fi
# Neither variable set means rpi-image-gen locks the account, and that is now the default this image
# ships: no password, nobody logs in, and the card carries no credential for anyone to look up.
#
# --password is for a card you are building for yourself and want to reach immediately. Note that
# rpi-image-gen validates it against a regex demanding upper, lower, digit and punctuation - the
# check the old stock password had to route around with a pre-computed hash, and a reasonable one to
# submit to for a password chosen deliberately at build time.
if [ -n "$password" ]; then
    overrides+=("IGconf_device_user1pass=$password")
elif [ -n "$ssh_key" ]; then
    # The one case where the boot file's gate stays open on a board somebody can already reach: a
    # key is a way in, an empty password field is not, and the gate reads the password field. That
    # is the right default - this is exactly the board that needs a way back if the key is lost -
    # but it is theirs to close, so say how rather than leaving it to be discovered.
    echo "==> No --password given; the $device_user account is locked and the key is the way in."
    echo "    homespool-login.txt on the card's boot partition can still give it a password, and"
    echo "    stays able to for as long as the account has none - which is the way back if the key"
    echo "    is ever lost, and the only thing that works at the console. To close it off, run"
    echo "    passwd once on the board."
else
    # Stated rather than warned about: no key and no password is what a card built for other people
    # is *meant* to be, and it is what a downloaded image ships as. Somebody building one for
    # themselves still wants to know before they flash it rather than after.
    echo "==> No --password and no --ssh-key: the $device_user account will ship LOCKED."
    echo "    The stack runs and serves its pages either way; what is missing is a shell on the"
    echo "    board. To get one, put a password and/or an SSH key in homespool-login.txt on the"
    echo "    card's boot partition - before first boot, or at any point afterwards, for as long as"
    echo "    the account still has neither."
fi
if [ -n "$ssh_key" ]; then
    cp "$ssh_key" "$work_dir/authorized_key.pub"
    overrides+=("IGconf_homespool_sshkey=/repo/pi/work/authorized_key.pub")
fi

# --privileged: mmdebstrap mounts pseudo-filesystems in private namespaces and genimage wants loop
# devices. -v on the repo rather than a copy, so a failed build can be re-run against an edit without
# rebuilding the payload.
run_imagegen() {
    docker run --rm --privileged \
        -v "$repo_root:/repo" \
        -v homespool-ig-work:/opt/rpi-image-gen/work \
        homespool-imagegen \
        ./rpi-image-gen build "$1" -S /repo/pi -c homespool.yaml -- "${overrides[@]}"
}

# ------------------------------------------------------------------------------------------------
# 4. Filesystem, then the container store, then the image.
#
# The split is the whole point. `docker load` on the board would cost every single install the same
# ~550 MB of unpacking and minutes of Pi 3 CPU, on an SD card, to reach a state identical on every
# card - so it is done once, here. -f stops after the root filesystem; -i resumes at image
# generation; in between, a real Docker daemon populates the store the board will boot with.
#
# It has to be a real daemon. /var/lib/docker is not a directory of files you can assemble by
# copying: overlay2's layer tree and the content store are daemon-managed, and a hand-built one is
# either ignored or corrupt. Running dockerd inside the chroot would need cgroups the chroot has
# not got, so instead dind runs as an ordinary container and is simply *pointed* at the chroot's
# /var/lib/docker with --data-root.
#
# --storage-driver=overlay2 is pinned rather than left to autodetect. The Pi will use overlay2, and
# a store written by a different driver is not an error there - the daemon just reports no images,
# which looks like the bake silently did nothing.
# ------------------------------------------------------------------------------------------------
echo "==> Building the root filesystem"
run_imagegen -f

echo "==> Pre-loading the container images into the card's Docker store"
# Newest first: a re-run leaves the previous chroot in the volume beside this one.
chroot_fs=$(docker run --rm -v homespool-ig-work:/w homespool-imagegen \
    sh -c 'ls -dt /w/chroot-*/filesystem 2>/dev/null | head -1' | tr -d '\r')
if [ -z "$chroot_fs" ]; then
    echo "No chroot filesystem found in the work volume - did -f actually succeed?" >&2
    exit 1
fi
echo "    store: $chroot_fs/var/lib/docker"

docker rm -f homespool-dind >/dev/null 2>&1 || true
docker run -d --name homespool-dind --privileged \
    -v homespool-ig-work:/w \
    -v "$images_dir:/images:ro" \
    docker:dind \
    dockerd --host=unix:///var/run/docker.sock \
            --data-root="$chroot_fs/var/lib/docker" \
            --storage-driver=overlay2 \
            --iptables=false --bridge=none >/dev/null

ready=""
for _ in $(seq 1 60); do
    if docker exec homespool-dind docker info >/dev/null 2>&1; then ready=y; break; fi
    sleep 1
done
if [ -z "$ready" ]; then
    echo "The build daemon never came up. Its log:" >&2
    docker logs homespool-dind >&2 || true
    docker rm -f homespool-dind >/dev/null 2>&1 || true
    exit 1
fi

docker exec homespool-dind docker load -i /images/homespool-images.tar.gz
echo "    loaded into the card's store:"
docker exec homespool-dind docker image ls --format '      {{.Repository}}:{{.Tag}} {{.Size}}'

# Stopped, not killed: dockerd flushes the content store's metadata on shutdown, and a store caught
# mid-write is exactly the silent "no images on the board" failure this pinning is meant to avoid.
docker stop homespool-dind >/dev/null
docker rm homespool-dind >/dev/null

echo "==> Generating the card image"
run_imagegen -i

# ------------------------------------------------------------------------------------------------
# 5. Out of the volume and onto the host, where rpi-imager can see it.
# ------------------------------------------------------------------------------------------------
# The *uncompressed* image out of the volume, not the .img.zst the deploy step produced beside it.
# We modify the image below, so taking the compressed copy would mean either shipping a .zst that
# disagrees with the .img next to it - a trap for anyone who keeps the archive and flashes it later -
# or decompressing, editing and recompressing, which is a third pass over 2.3 GB to reach the same
# place. genimage leaves the raw image in work/image-<name>/, so we take that and compress it once
# ourselves, after the edit.
echo "==> Extracting the image"
mkdir -p "$work_dir/out"
docker run --rm \
    -v homespool-ig-work:/work \
    -v "$work_dir/out:/out" \
    homespool-imagegen \
    sh -c "cp -v /work/image-${image_name}/${image_name}.img /out/"

# ------------------------------------------------------------------------------------------------
# 6. noatime, which can only be done here.
#
# Root otherwise mounts rw,relatime,errors=remount-ro,commit=30. Those options are a *literal* inside
# a heredoc in image/mbr/simple_dual/setup.sh - no variable, nothing configurable - and that script
# writes /etc/fstab with a truncating `>` during image generation, so it destroys whatever the rootfs
# stage put there. That is why this was written off as unreachable: a customize-hook cannot win a race
# against a later stage that overwrites the file wholesale.
#
# Editing the finished image happens *after* that write, so nothing regenerates it. The two routes the
# notes had considered - forking the image layer into pi/image/, which then drifts on every upstream
# pin bump, or a first-boot sed on the running board, which is permanent machinery on every card -
# both cost more than this.
#
# The offset is read from the partition table rather than hardcoded. It moved once already: the
# combined image's boot partition grew 104 MB -> 152 MB to hold the second kernel and initramfs,
# taking root from sector 229376 to 327680, and a stale constant reports that as
# "Bad magic number in super-block" - which reads like a corrupt image rather than arithmetic.
#
# --privileged for loop devices, the same reason the image build needs it. The sed is anchored on the
# by-slot device so it cannot touch the /boot/firmware line, and the result is grepped back: a silent
# no-op here would ship the default and nobody would notice until they measured card wear.
echo "==> Setting noatime on the root filesystem"
docker run --rm --privileged \
    -v "$work_dir/out:/out" \
    homespool-imagegen \
    bash -euc "
        img=/out/${image_name}.img
        # \$1 ~ /img2\$/ rather than a bare /img2\$/ pattern: the latter anchors to the end of the
        # *line*, which is the start sector, not the device name - so it silently matches nothing.
        start=\$(fdisk -l -o Device,Start \"\$img\" | awk '\$1 ~ /img2\$/ {print \$2}')
        [ -n \"\$start\" ] || { echo 'could not find partition 2 in the image' >&2; exit 1; }
        dev=\$(losetup -o \$((start * 512)) -f --show \"\$img\")
        trap 'umount /mnt 2>/dev/null || true; losetup -d \"\$dev\" 2>/dev/null || true' EXIT
        mount -t ext4 \"\$dev\" /mnt
        sed -i 's|^\(/dev/disk/by-slot/system  */  *ext4 [^ ]*\)relatime|\1noatime|' /mnt/etc/fstab
        grep -q 'by-slot/system.*noatime' /mnt/etc/fstab \
            || { echo 'noatime was not applied - fstab format changed?' >&2; exit 1; }
        grep '^/dev' /mnt/etc/fstab | sed 's/^/    /'
    "

# Decompressed here rather than left to whoever flashes it, because Raspberry Pi Imager's "Use
# custom" option accepts a .img and nothing else - the dialog says so in as many words. Its source
# does list .zst, but on the *download* path, where it fetches an OS from its own list; the
# local-file picker does not decompress.
#
# Handing it a .zst produced a card with no partition table at all, three times, while the identical
# image written uncompressed booted. Imager reported "Write Successful" each time, and was telling
# the truth: it verifies the card against what it decided to write, which stays correct when what it
# read was compressed bytes.
#
# Compressed *from the edited image*, which is the whole reason the raw one was taken out of the
# volume above: the .zst here is an archive of what we actually ship, not of what genimage produced
# before the fstab edit. -T0 for every core, since this is now the last slow step.
# ------------------------------------------------------------------------------------------------
# 7. Assert the owner can still become root.
#
# Checked against the finished image, because the layer cannot check itself: rpi-image-gen runs our
# customize-hooks before device-user-admin's `dpkg --purge sudo`, so anything asserted inside the
# layer would be asserted too early to see the thing that removes it.
#
# Not hypothetical, and that is why it is worth a build step. Sudo was absent from every card built
# between dropping the stock password and adding the override above, and nothing reported it. The
# purge that would remove it fails today for two reasons, both accidents from here: raspinfo happens
# to depend on sudo, and sudo's own prerm refuses to remove itself while root has no password - which
# is one SUDO_FORCE_REMOVE=yes away from not being true.
#
# Env vars rather than interpolating into the script body: this needs the user name and the image
# name inside a shell that also uses $1, $2 and awk fields, and the escaping in the noatime step
# above is already at the limit of what is readable.
echo "==> Checking the device user can become root"
docker run --rm --privileged \
    -e DEVICE_USER="$device_user" \
    -e IMAGE_NAME="$image_name" \
    -v "$work_dir/out:/out" \
    homespool-imagegen \
    bash -euc '
        img=/out/$IMAGE_NAME.img
        start=$(fdisk -l -o Device,Start "$img" | awk "\$1 ~ /img2\$/ {print \$2}")
        [ -n "$start" ] || { echo "could not find partition 2 in the image" >&2; exit 1; }
        dev=$(losetup -o $((start * 512)) -f --show "$img")
        trap "umount /mnt 2>/dev/null || true; losetup -d $dev 2>/dev/null || true" EXIT
        mount -t ext4 "$dev" /mnt

        [ -x /mnt/usr/bin/sudo ] \
            || { echo "sudo is not installed in the image - the purge hook succeeded" >&2; exit 1; }

        # The member list, matched whole: a substring test would accept "pi" inside "pixel".
        members=$(awk -F: "\$1 == \"sudo\" { print \$4 }" /mnt/etc/group)
        case ",$members," in
            *",$DEVICE_USER,"*) ;;
            *) echo "$DEVICE_USER is not in the sudo group (members: ${members:-none})" >&2; exit 1 ;;
        esac

        grep -rqs "^$DEVICE_USER[[:space:]]" /mnt/etc/sudoers.d/ \
            || { echo "no sudoers.d grant for $DEVICE_USER" >&2; exit 1; }

        echo "    sudo present, group members: $members"
        grep -rhs "^$DEVICE_USER[[:space:]]" /mnt/etc/sudoers.d/ | sed "s/^/    /"
    '

echo "==> Compressing an archive copy (the .img is the artefact to flash - see the comment above)"
docker run --rm -v "$work_dir/out:/out" homespool-imagegen \
    sh -c "zstd -q -f -T0 /out/${image_name}.img -o /out/${image_name}.img.zst"

echo
echo "Done. Written to pi/work/out:"
ls -lh "$work_dir/out"
echo
echo "Flash ${image_name}.img with Raspberry Pi Imager (Use Custom)."
echo "Do NOT flash the .zst - it verifies clean and produces an unreadable card."
echo "Then browse to http://homespool.local once the first boot settles."
