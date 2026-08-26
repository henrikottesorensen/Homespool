#!/usr/bin/env bash
#
# Build and run a Buddy connect-rig target in the container. See notes/buddy-rig.md.
#
#   ./rig/rig.sh build connect_rig            # configure if needed, then build the target
#   ./rig/rig.sh run connect_rig --help       # run a built binary, passing arguments through
#   ./rig/rig.sh shell                        # poke around
#
#   ./rig/rig.sh --printer XL build connect_rig      # an XL instead of the default MINI
#   ./rig/rig.sh --printer INDX build connect_rig    # a Core One with INDX heads
#   ./rig/rig.sh --printer XL run connect_rig --identity /identity.json --host host.docker.internal \
#       --port 5052 --tools '1:PLA,2:PETG,3:-,5:ASA' --active 1
#
# --printer decides what the binary IS: the printer_type it reports, and whether a filament gcode
# leaves a tool picked (only INDX docks). It does NOT decide the tool count - the test tree's
# MarlinConfigPre stub pins that at five for every configuration. It must come before the verb, and
# it selects its own build volume so the builds cannot overwrite each other.
#
# A thin wrapper over compose.yaml, which owns the mounts, the build volume and the image. What is
# left here is argument handling, the checks worth failing early on, and the two-step "build then
# run" that compose has no single verb for. A new mount goes in compose.yaml and needs no edit here.
#
# Rewritten 2026-08-09. It used to spell out its own `docker run` with the mount list inline, which
# was fine while it was the only caller - and stopped being fine when compose.yaml arrived with the
# TLS mounts, because the same mounts were then defined twice and free to drift.
set -euo pipefail

RIG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# The printer the rig is built as. A closed set, because RIG_BOARD and RIG_PRINTER have to agree and
# the firmware tree does not check them against each other - a mismatched pair configures cleanly and
# then fails to build. Adding one here is how you add one at all.
printer="${RIG_PRINTER:-MINI}"

if [ "${1:-}" = "--printer" ]; then
    printer="${2:?usage: rig.sh --printer <MINI|XL|INDX> ...}"
    shift 2
fi

case "$printer" in
    MINI)
        export RIG_BOARD=BUDDY RIG_PRINTER=MINI
        # No printer suffix, so an existing MINI build volume stays the one that gets used.
        : "${BUILD_VOLUME:=connect_rig-builddir}"
        ;;
    XL)
        export RIG_BOARD=XLBUDDY RIG_PRINTER=XL
        # XL is in PRINTERS_WITH_RESOURCES, which collides with the test tree's own translation
        # targets; compose.yaml carries the diagnosis.
        export RIG_TRANSLATIONS=NO
        : "${BUILD_VOLUME:=connect_rig-xl-builddir}"
        ;;
    INDX)
        # COREONE_INDX - a Core One with INDX heads, so BOARD is XBUDDY and not a board of its own
        # (INDX_HEAD is the satellite's firmware, not the mainboard's).
        #
        # The one build where a filament gcode DOCKS when it finishes: HAS_INDX() is 1, so the rig
        # ends up with nothing picked and the next M701/M702 carrying no T does nothing. That is the
        # behaviour an XL build cannot show, and the reason to have this configuration at all.
        export RIG_BOARD=XBUDDY RIG_PRINTER=COREONE_INDX
        export RIG_TRANSLATIONS=NO
        : "${BUILD_VOLUME:=connect_rig-indx-builddir}"
        ;;
    *)
        echo "rig: unknown printer '$printer' - MINI, XL or INDX" >&2
        exit 1
        ;;
esac

# A build volume holds one tree configured as one printer, and the configure step only runs when
# build.ninja is absent - so sharing a volume between printers does not rebuild, it silently keeps
# the first one. Exported because compose reads it as an interpolation.
export BUILD_VOLUME

# Every Prusa checkout lives under ~/Prusa (moved there 2026-07-31); this used to point straight at
# the home directory. Override FIRMWARE_DIR if yours is somewhere else - the check below says so.
# Exported rather than passed, because compose reads them as ${...} interpolations.
export FIRMWARE_DIR="${FIRMWARE_DIR:-$HOME/Prusa/Prusa-Firmware-Buddy}"
export USB_DIR="${USB_DIR:-$RIG_DIR/usb}"
# connect_rig needs an enrolled identity (Homespool.FakePrinter.Cli enroll writes one). Mounted at a
# fixed path inside the container so `--identity /identity.json` always works regardless of where it
# lives on the host.
export IDENTITY_FILE="${IDENTITY_FILE:-$RIG_DIR/identity.json}"
# Holds connect.der, the trust anchor for --custom-cert. Mint it fresh; see compose.yaml.
export CONNECT_DIR="${CONNECT_DIR:-$RIG_DIR/lab}"

if [ ! -d "$FIRMWARE_DIR/src/connect" ]; then
    echo "rig: no firmware checkout at $FIRMWARE_DIR - set FIRMWARE_DIR" >&2
    exit 1
fi

# Created here rather than left to docker, which would make them root-owned.
mkdir -p "$USB_DIR" "$CONNECT_DIR"

# compose mounts the identity unconditionally, and docker answers a missing bind source by creating
# a directory at it - so an absent identity.json would come back as identity.json/, sitting exactly
# where the real file needs to go. /dev/null is a real file, reads as empty, and gets the rig to say
# it carries no Fingerprint/Token, which is the honest message. build and shell never read it.
if [ ! -f "$IDENTITY_FILE" ]; then
    echo "rig: no identity at $IDENTITY_FILE - continuing without one" >&2
    export IDENTITY_FILE=/dev/null
fi

compose() {
    # -f rather than a cd, so `./rig/rig.sh` keeps working from the repo root. Compose resolves the
    # relative paths inside the file against the file's own directory either way.
    docker compose -f "$RIG_DIR/compose.yaml" "$@"
}

# `docker compose run` allocates a pseudo-TTY when it can, and -T turns that off. Needed whenever
# there is no terminal on both ends - a redirect, a pipe, or CI - which is the same condition the
# old `docker run -it` had to guard. Kept as a plain string rather than an array: macOS ships bash
# 3.2, where an empty array expansion trips `set -u`.
tty_flags=""

if [ ! -t 0 ] || [ ! -t 1 ]; then
    tty_flags="-T"
fi

# Compose builds an image only when none exists, so a Dockerfile edit would otherwise never reach a
# machine that has built once. This is its own step rather than a --build on the runs below, because
# buildkit writes its progress to STDOUT where compose's own chatter goes to stderr - so `--build`
# anywhere near `run` silently corrupts `rig.sh run connect_render_dump > render-fixtures.json`.
# Cheap when there is nothing to do.
refresh_image() {
    compose build >&2
}

# Build progress goes to stderr, always: it is diagnostic, and keeping stdout clean is what lets
# that redirect work.
configure_and_build() {
    refresh_image
    # shellcheck disable=SC2086 # deliberate word splitting of the flags above
    compose run --rm $tty_flags build "$1" >&2
}

case "${1:-}" in
    build)
        configure_and_build "${2:?usage: rig.sh build <target>}"
        ;;
    run)
        target="${2:?usage: rig.sh run <target> [args...]}"
        shift 2
        configure_and_build "$target"
        # The connect_rig service is the container to run a target in; the entrypoint override is
        # what makes it any target rather than only the one it is named for. Homespool runs on the
        # host, and compose.yaml's extra_hosts entry is how the container reaches it.
        # shellcheck disable=SC2086 # deliberate word splitting of the flags above
        compose run --rm $tty_flags \
            --entrypoint "/build/tests/unit/connect/$target" connect_rig "$@"
        ;;
    shell)
        refresh_image
        # shellcheck disable=SC2086 # deliberate word splitting of the flags above
        compose run --rm $tty_flags --entrypoint bash connect_rig
        ;;
    *)
        echo "usage: rig.sh [--printer <MINI|XL|INDX>] {build <target>|run <target> [args...]|shell}" >&2
        exit 1
        ;;
esac
