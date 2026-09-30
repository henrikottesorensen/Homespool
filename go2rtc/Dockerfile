# The camera sidecar: go2rtc, built here from its released source rather than pulled from Docker Hub.
#
# The release is the one alexxit/go2rtc publishes; what is ours is everything around it. Upstream's
# image is rebuilt only when a release is cut, and releases stopped for months at a time, so 1.9.14's
# carried a Go standard library, golang.org/x/crypto and golang.org/x/net with published fixes in the
# binary that serves HTTP, RTSP and WebRTC, on an Alpine that had moved on. A build of our own takes
# the current Go toolchain, those two modules at their latest, and the current Alpine every time it
# runs - and, since a floating choice made at build time cannot be read back afterwards, it is given
# each of them already resolved and records what it was given: the base image's digest as the other
# two images do, and the toolchain's digest and the modules' versions in labels of its own.
#
# The release is named twice, by tag and by commit, and the build refuses a tag that no longer points
# where it did: a tag can be moved, a commit cannot.
ARG HOMESPOOL_BASE_IMAGE=alpine:3
ARG HOMESPOOL_BASE_DIGEST=
ARG GO2RTC_VERSION=v1.9.14
ARG GO2RTC_COMMIT=b5948cfb25404cc5cb37b166ecaa2dca20b11d4b

# The toolchain, by floating tag so each build takes the newest Go and so the newest standard
# library - which, unlike the application's SDK, is compiled into the binary that ships. So it is
# resolved to a digest first, like a base image (tools/base-digest.sh with this ARG's name), built
# on exactly that, and recorded. Without a digest, as a bare `docker compose build` leaves it, this
# builds on the tag and the label says nothing.
ARG HOMESPOOL_BUILDER_IMAGE=golang:1-alpine
ARG HOMESPOOL_BUILDER_DIGEST=

# The Go modules taken at a newer release than go2rtc's own go.mod names - see the go get below for
# why only these - and the exact versions, as tools/go-module-versions.sh resolves them from this
# list: `module@version ...`. Empty takes each at its latest release at build time, and the label
# says nothing.
ARG GO2RTC_UPDATED_MODULES="golang.org/x/crypto golang.org/x/net"
ARG GO2RTC_MODULE_VERSIONS=

# Compiled on the build machine for the target, not emulated: go2rtc needs no cgo, so an arm64 image
# builds on an amd64 runner at native speed.
FROM --platform=$BUILDPLATFORM ${HOMESPOOL_BUILDER_IMAGE}${HOMESPOOL_BUILDER_DIGEST:+@${HOMESPOOL_BUILDER_DIGEST}} AS build
ARG TARGETOS
ARG TARGETARCH
ARG GO2RTC_VERSION
ARG GO2RTC_COMMIT
ARG GO2RTC_UPDATED_MODULES
ARG GO2RTC_MODULE_VERSIONS

# For the clone and for applying the patches, and nothing that reaches the binary: the clone is
# checked against the release's full commit below, and git apply either applies a patch exactly or
# fails the build. So which git Alpine serves today changes nothing that ships, which is why it is
# not pinned - and neither image here carries a patch tool that could replace it.
RUN apk add --no-cache git

WORKDIR /src
RUN git clone --quiet --depth 1 --branch "$GO2RTC_VERSION" https://github.com/AlexxIT/go2rtc.git .
RUN actual="$(git rev-parse HEAD)" && \
    if [ "$actual" != "$GO2RTC_COMMIT" ]; then \
        echo "go2rtc $GO2RTC_VERSION is $actual, not $GO2RTC_COMMIT" >&2; exit 1; \
    fi

# Fixes not in the release, applied after the check so they are applied to exactly that commit, in
# order - each patch is made on top of the one before it. Each one's message says why in full.
#
# 0001: stream.mjpeg passes a USB camera's frames on without the Huffman tables the camera leaves
# out, which Safari cannot decode - the stream arrives and nothing is painted. frame.jpeg already
# adds them; the patch makes stream.mjpeg add them too, but only them: frame.jpeg's repair also
# re-encodes a frame whose header it does not know, which on a stream costs a Raspberry Pi 3 a full
# core per viewer.
#
# 0002: adding a camera to a go2rtc.yaml whose streams entry reads `{}` or `null` wrote a file that
# no longer parsed, so the camera was refused. That is what an older setup-env.sh seeded, and what
# the config becomes after the last camera is deleted and Homespool next writes its WebRTC settings.
#
# 0003: writing to a go2rtc.yaml that was empty, comment-only or null panicked the request instead,
# and the write Homespool makes at startup is exactly that - so a deployment that had never added a
# camera lost its WebRTC settings, and with them live view.
#
# 0004: the writer placed text by counting LF, and the YAML library that reports where each key is
# also counts U+2028 and U+2029 - which it writes into a quoted value unescaped. One camera source
# holding either put every key after it a line out, so a later camera could not be saved and deleting
# one removed its neighbour. Homespool refuses such a source; the patch mends a file written before
# it did.
#
# Every patch by name, so one added to the directory cannot be skipped by accident; git apply fails
# the build on a patch that no longer fits.
COPY patches/0001-Add-missing-Huffman-tables-to-stream.mjpeg-frames.patch /patches/
RUN git apply --verbose /patches/0001-Add-missing-Huffman-tables-to-stream.mjpeg-frames.patch
COPY patches/0002-Patch-into-an-empty-or-flow-style-parent.patch /patches/
RUN git apply --verbose /patches/0002-Patch-into-an-empty-or-flow-style-parent.patch
COPY patches/0003-Merge-a-config-PATCH-into-an-empty-config-file.patch /patches/
RUN git apply --verbose /patches/0003-Merge-a-config-PATCH-into-an-empty-config-file.patch
COPY patches/0004-Break-lines-where-yaml.v3-does-when-splicing-the-config.patch /patches/
RUN git apply --verbose /patches/0004-Break-lines-where-yaml.v3-does-when-splicing-the-config.patch

# The two modules a scanner found fixes for, at their latest release - and only those two, because
# they are the Go team's own and keep their API; the rest stay at what the release was tested with.
# GOTOOLCHAIN stops go.mod asking for a toolchain download: this one is newer than the release needs.
#
# At the versions given when there are any, so the layer is rebuilt when a module has a new release
# and not otherwise; at @latest only for a build given none.
ENV GOTOOLCHAIN=local CGO_ENABLED=0
RUN go get ${GO2RTC_MODULE_VERSIONS:-$(for module in $GO2RTC_UPDATED_MODULES; do \
                                        printf '%s@latest ' "$module"; done)}

# Each patch carries a test of its own, run here against the modules the image ships and on the
# machine doing the build, so a go2rtc release the patches still apply to but no longer work on fails
# the build rather than a viewer. 0001's matters most: nothing else anywhere notices its absence - a
# test pattern arrives with its tables whether it is there or not - and without it Safari paints
# nothing.
RUN go test ./pkg/mjpeg/ ./pkg/yaml/ ./internal/api/

# Upstream's own flags. The version it reports is written into main.go, so it still says 1.9.14.
RUN GOOS="$TARGETOS" GOARCH="$TARGETARCH" go build -trimpath -ldflags "-s -w" -o /out/go2rtc .

FROM ${HOMESPOOL_BASE_IMAGE}${HOMESPOOL_BASE_DIGEST:+@${HOMESPOOL_BASE_DIGEST}}

# ffmpeg for USB cameras and anything that needs transcoding, and tini to reap the ffmpeg processes
# go2rtc starts - upstream's entrypoint too. Nothing else from upstream's list: python, bash, curl and
# jq are there for exec: and echo: sources, and Homespool registers only rtsp, rtsps, http, https,
# rtmp and ffmpeg devices. Each would be one more package for a scanner to report and for somebody to
# establish is never run.
#
# The upgrade takes Alpine's fixes since the base image was cut, and HOMESPOOL_PACKAGE_REFRESH is what
# makes it run: any ARG in scope becomes part of the key of every RUN after it, so ./build.sh setting
# it to the date reruns this at most once a day, where an unchanged base would otherwise reuse the
# first build's layer forever.
ARG HOMESPOOL_PACKAGE_REFRESH=
RUN apk upgrade --no-cache && \
    apk add --no-cache ffmpeg tini

COPY --from=build /out/go2rtc /usr/local/bin/go2rtc

# Root's, like the rest of the image: compose bind-mounts the one file go2rtc writes over
# /config/go2rtc.yaml, and go2rtc rewrites it in place. No VOLUME, unlike upstream's image - it would
# only leave an anonymous volume behind per container for a directory nothing else is written to.
WORKDIR /config

# Not root, as compose runs it anyway; compose sets the group, which has to be the host's video GID.
USER 65532

EXPOSE 1984 8554 8555 8555/udp
ENTRYPOINT ["/sbin/tini", "--"]
CMD ["go2rtc", "-config", "/config/go2rtc.yaml"]

# Blank rather than left out where there is nothing true to say, as in the other two images. licenses
# is go2rtc's: the image is its binary and Alpine's packages, and this file adds none of its own code.
ARG HOMESPOOL_GITREF=
ARG HOMESPOOL_VERSION=
ARG HOMESPOOL_BASE_IMAGE
ARG HOMESPOOL_BASE_DIGEST
ARG GO2RTC_VERSION
ARG GO2RTC_COMMIT
ARG HOMESPOOL_BUILDER_IMAGE
ARG HOMESPOOL_BUILDER_DIGEST
ARG GO2RTC_MODULE_VERSIONS
LABEL org.opencontainers.image.title="Homespool camera sidecar" \
      org.opencontainers.image.description="go2rtc ${GO2RTC_VERSION} (${GO2RTC_COMMIT}) with the fixes in go2rtc/patches, built from source for Homespool, a self-hosted alternative to Prusa Connect." \
      org.opencontainers.image.source="https://github.com/henrikottesorensen/Homespool" \
      org.opencontainers.image.url="https://github.com/henrikottesorensen/Homespool" \
      org.opencontainers.image.revision="${HOMESPOOL_GITREF}" \
      org.opencontainers.image.version="${HOMESPOOL_VERSION}" \
      org.opencontainers.image.created="" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.base.name="${HOMESPOOL_BASE_IMAGE}" \
      org.opencontainers.image.base.digest="${HOMESPOOL_BASE_DIGEST}"

# What OCI has no key for, under a prefix this project controls: the toolchain the binary was compiled
# with, the modules taken newer than go2rtc's own, and the release they were applied to.
LABEL io.github.henrikottesorensen.homespool.builder.name="${HOMESPOOL_BUILDER_IMAGE}" \
      io.github.henrikottesorensen.homespool.builder.digest="${HOMESPOOL_BUILDER_DIGEST}" \
      io.github.henrikottesorensen.homespool.go.modules="${GO2RTC_MODULE_VERSIONS}" \
      io.github.henrikottesorensen.homespool.go2rtc.version="${GO2RTC_VERSION}" \
      io.github.henrikottesorensen.homespool.go2rtc.commit="${GO2RTC_COMMIT}"
