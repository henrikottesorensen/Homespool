#!/usr/bin/env bash
# Builds a Raspberry Pi 3 SD-card image with the Homespool stack baked in.
#
#   pi/build.sh --ssh-key ~/.ssh/id_ed25519.pub
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

while [ $# -gt 0 ]; do
    case "$1" in
        --ssh-key)   ssh_key="$2"; shift 2 ;;
        --password)  password="$2"; shift 2 ;;
        --user)      device_user="$2"; shift 2 ;;
        -h|--help)   sed -n '2,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

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
for layer in "$pi_dir"/layer/*.yaml; do
    bad=$(awk '/^# METABEGIN/{f=1;next} /^# METAEND/{f=0} f{
              if ($0 ~ /^#$/ || $0 ~ /^#  / || $0 ~ /^# [A-Za-z][A-Za-z0-9-]*:/) next
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

# The stock password, when none is given. The OctoPi arrangement, and adopted for the reason OctoPi
# has it: rpi-image-gen's own default is to *lock* the account, which is correct for a fleet and
# useless for an appliance - a board that will not join the network is then also a board you cannot
# log into to find out why, and the only way in is editing cmdline.txt to get an init=/bin/sh shell.
# That happened here, which is what settled it.
#
# The mitigation for a password everybody knows is that the layer expires it: the first login has to
# change it before it does anything else.
DEFAULT_PASSWORD="homespool"

# ------------------------------------------------------------------------------------------------
# 1. The application images, native arm64.
# ------------------------------------------------------------------------------------------------
echo "==> Building the Homespool container images for arm64"
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
# Only the three files compose bind-mounts. The rest of nginx/ is baked into the proxy image already.
cp "$repo_root/nginx/homespool.conf"         "$payload_dir/nginx/"
cp "$repo_root/nginx/homespool-proxy.conf"   "$payload_dir/nginx/"
cp "$repo_root/nginx/homespool-printer.conf" "$payload_dir/nginx/"

# Deliberately NOT into the payload. This tarball never reaches the card: it is loaded into the
# card's Docker store during the build (step 4), so the Pi boots with the images already unpacked.
# Shipping it as well would put ~200 MB on the card that exists only to be expanded and deleted.
echo "==> Saving the container images (the slow part, ~550 MB uncompressed)"
mkdir -p "$images_dir"
docker save printerservice:latest homespool-proxy:latest \
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
)
# A hash rather than the plain variable, because rpi-image-gen validates plain passwords against a
# regex demanding upper, lower, digit and punctuation - which "homespool" is not, and which is the
# wrong trade for a credential whose whole job is to be typed once and replaced. The hash path has
# no such check. Generated in the builder because macOS ships LibreSSL, whose passwd has no -6.
if [ -n "$password" ]; then
    overrides+=("IGconf_device_user1pass=$password")
else
    echo "==> No --password given; using the stock default, expired on first login"
    default_hash=$(docker run --rm homespool-imagegen openssl passwd -6 "$DEFAULT_PASSWORD")
    overrides+=("IGconf_device_user1passhash=$default_hash")
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
        ./rpi-image-gen build "$1" -S /repo/pi -c homespool-pi3.yaml -- "${overrides[@]}"
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
echo "==> Extracting the image"
mkdir -p "$work_dir/out"
docker run --rm \
    -v homespool-ig-work:/work \
    -v "$work_dir/out:/out" \
    homespool-imagegen \
    sh -c 'cp -v /work/deploy-*/homespool-pi3.img.zst /out/ 2>/dev/null || cp -v /work/deploy-*/*.img.zst /out/'

# Decompressed here rather than left to whoever flashes it, because the compressed form does not
# survive the trip. Raspberry Pi Imager accepts .zst - its source lists the extension and dispatches
# to a zstd decompressor - but a card written from ours came out with no partition table at all,
# three times, while the identical image written uncompressed booted. Imager reported "Write
# Successful" every time, and would: it verifies that the card matches what it decided to write,
# which stays true when the decompression ahead of that produced the wrong bytes.
#
# So the .img is what you flash and the .zst is what you keep. Root cause in the compressed stream
# is unidentified - if it is ever chased, start by comparing `zstd -d` output against what Imager
# writes, since our own zstd round-trips correctly.
echo "==> Decompressing (the .img is the artefact to flash - see the comment above)"
docker run --rm -v "$work_dir/out:/out" homespool-imagegen \
    sh -c 'zstd -d -f /out/homespool-pi3.img.zst -o /out/homespool-pi3.img' >/dev/null 2>&1

echo
echo "Done. Written to pi/work/out:"
ls -lh "$work_dir/out"
echo
echo "Flash homespool-pi3.img with Raspberry Pi Imager (Use Custom)."
echo "Do NOT flash the .zst - it verifies clean and produces an unreadable card."
echo "Then browse to http://homespool.local once the first boot settles."
