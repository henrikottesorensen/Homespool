# Build environment for the Buddy connect rig - the real Prusa firmware Connect client, built as a
# host binary and pointed at Homespool. See notes/buddy-rig.md.
#
# This lives here rather than in the firmware checkout on purpose: the rig branch there is shaped to
# be offerable upstream, and a Homespool-specific Dockerfile would undo that.
#
# Why a container at all, when the rig already builds on macOS: the transfer phase needs a writable
# /usb, which firmware hard-codes (transfer.hpp:315, planner.cpp:135-141) and which cannot be created
# on a read-only APFS root volume. Linux is also simply the platform this tree expects - its
# -fno-extended-identifiers flag is GCC-only, and its --gc-sections link flag takes the GNU branch
# here rather than the macOS special case.
FROM ubuntu:26.04

# cmake/ninja come from the distro rather than from utils/bootstrap.py. Verified sufficient: the
# whole test tree configures and builds with them. bootstrap.py is all-or-nothing - its only scoping
# flag ADDS packages - so using it would mean fetching ~1.2 GB of ARM toolchain, bootloaders and an
# emulator to build host binaries that need none of it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential \
        cmake \
        ninja-build \
        python3 \
        python3-requests \
        git \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# The transfer destination firmware insists on. Bind-mount a host directory over it to inspect what
# actually landed; leaving it unmounted still works, the files just stay in the container.
RUN mkdir -p /usb

# The source is mounted read-only, so git refuses it as dubious ownership before CMake can read the
# version from it.
RUN git config --global --add safe.directory /src

WORKDIR /build
