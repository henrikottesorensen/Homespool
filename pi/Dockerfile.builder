# The machine that builds the SD-card image.
#
# rpi-image-gen's supported host is 64-bit Raspberry Pi OS - Debian arm64. This image is the closest
# honest approximation: Debian trixie, arm64, with the tool's own install_deps.sh run against it. On
# an Apple Silicon Mac that runs natively under Docker Desktop, so nothing is emulated and the build
# takes minutes rather than hours. On an x86 host it would need qemu and is not worth attempting.
#
# It must be run --privileged. mmdebstrap builds chroots by mounting pseudo-filesystems inside
# private namespaces, and genimage needs loop devices; both want CAP_SYS_ADMIN. Containers are not a
# formally supported host for this tool - it works, but that is the reason this file pins a SHA
# rather than tracking a branch.
FROM debian:trixie

# The package lists are deliberately NOT cleaned here. install_deps.sh below installs with apt and
# does not update first, so wiping the lists in this layer leaves it unable to resolve a single
# package - which apt reports as a bare exit 100, naming nothing.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      git ca-certificates curl sudo uidmap zstd

# Pinned, not a branch. The house rule for the firmware repos applies for the same
# reason here: this tool is under active development, its layer names and variable spellings are what
# pi/layer/homespool.yaml is written against, and a silent upstream rename would fail the build in a
# place that names neither.
ARG RPI_IMAGE_GEN_REF=8152398e473c1e66e7581c15a8f7162fdc46ff6d
RUN git clone https://github.com/raspberrypi/rpi-image-gen.git /opt/rpi-image-gen \
 && git -C /opt/rpi-image-gen checkout --detach "$RPI_IMAGE_GEN_REF"

# Its own dependency list, run as the tool intends. Kept as a separate layer so it caches on the SHA
# above rather than on anything in this repository.
RUN cd /opt/rpi-image-gen && ./install_deps.sh \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/rpi-image-gen
