#!/usr/bin/env bash
#
# Build and run a Buddy connect-rig target in the container. See notes/buddy-rig.md.
#
#   ./rig/rig.sh build connect_rig            # configure if needed, then build the target
#   ./rig/rig.sh run connect_rig --help       # run a built binary, passing arguments through
#   ./rig/rig.sh shell                        # poke around
#
# The firmware checkout is mounted READ-ONLY: the container cannot write to it, which is what keeps
# a Linux build from leaving artifacts that break the macOS one. The build directory lives in a
# named volume instead, so incremental builds survive between runs.
set -euo pipefail

FIRMWARE_DIR="${FIRMWARE_DIR:-$HOME/Prusa-Firmware-Buddy}"
USB_DIR="${USB_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/usb}"
# connect_rig needs an enrolled identity (Homespool.FakePrinter.Cli enroll writes one). Mounted at a
# fixed path inside the container so `--identity /identity.json` always works regardless of where it
# lives on the host.
IDENTITY_FILE="${IDENTITY_FILE:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/identity.json}"
IMAGE="${IMAGE:-homespool-rig}"
BUILD_VOLUME="${BUILD_VOLUME:-homespool-rig-build}"

if [ ! -d "$FIRMWARE_DIR/src/connect" ]; then
    echo "rig: no firmware checkout at $FIRMWARE_DIR - set FIRMWARE_DIR" >&2
    exit 1
fi

mkdir -p "$USB_DIR"

docker build -q -t "$IMAGE" "$(dirname "${BASH_SOURCE[0]}")" >/dev/null

run_in_container() {
    # Interactive only when there is a terminal to be interactive with - `docker run -it` is a hard
    # error otherwise, which would make this unusable from a script or CI. Kept as a plain string
    # rather than an array: macOS ships bash 3.2, where an empty array expansion trips `set -u`.
    local tty_flags=""

    if [ -t 0 ] && [ -t 1 ]; then
        tty_flags="-it"
    fi

    # Only mount an identity if one exists - the build and shell paths do not need it, and a missing
    # file would otherwise make docker create a directory at that path.
    local identity_mount=""

    if [ -f "$IDENTITY_FILE" ]; then
        identity_mount="-v $IDENTITY_FILE:/identity.json:ro"
    fi

    # shellcheck disable=SC2086 # deliberate word splitting of the flags above
    docker run --rm $tty_flags $identity_mount \
        -v "$FIRMWARE_DIR":/src:ro \
        -v "$BUILD_VOLUME":/build \
        -v "$USB_DIR":/usb \
        --add-host=host.docker.internal:host-gateway \
        "$IMAGE" "$@"
}

# Build progress goes to stderr, always: it is diagnostic, and keeping stdout clean is what lets
# `rig.sh run connect_render_dump > render-fixtures.json` work.
configure_and_build() {
    run_in_container bash -c "
        set -eu
        if [ ! -f /build/build.ninja ]; then
            # -DPython3_EXECUTABLE is not optional. Without it the tree pins
            # Python3_FIND_VIRTUALENV=ONLY to <source>/.venv (cmake/Utilities.cmake:9-13) and finds
            # nothing usable. BUDDY_NO_VIRTUALENV looks like the way to disable that but cannot
            # work - it is stored as a generator expression that a configure-time if() reads as a
            # literal string.
            cmake /src -G Ninja \
                -DBOARD=BUDDY \
                -DDSDL_TRANSPILE=OFF \
                -DPython3_EXECUTABLE=/usr/bin/python3
        fi
        ninja '$1'
    " >&2
}

case "${1:-}" in
    build)
        configure_and_build "${2:?usage: rig.sh build <target>}"
        ;;
    run)
        target="${2:?usage: rig.sh run <target> [args...]}"
        shift 2
        configure_and_build "$target"
        # Homespool runs on the host; host.docker.internal is how the container reaches it.
        run_in_container "/build/tests/unit/connect/$target" "$@"
        ;;
    shell)
        run_in_container bash
        ;;
    *)
        echo "usage: rig.sh {build <target>|run <target> [args...]|shell}" >&2
        exit 1
        ;;
esac
