# The camera sidecar: go2rtc, built here from its released source rather than pulled from Docker Hub.
#
# The release is the one alexxit/go2rtc publishes; what is ours is everything around it. Upstream's
# image is rebuilt only when a release is cut, and releases stopped for months at a time, so 1.9.14's
# carried a Go standard library, golang.org/x/crypto and golang.org/x/net with published fixes in the
# binary that serves HTTP, RTSP and WebRTC, on an Alpine that had moved on. A build of our own takes
# the current Go toolchain, those two modules at their latest, and the current Alpine every time it
# runs, and records its base the way the other two images do.
#
# The release is named twice, by tag and by commit, and the build refuses a tag that no longer points
# where it did: a tag can be moved, a commit cannot.
ARG HOMESPOOL_BASE_IMAGE=alpine:3
ARG HOMESPOOL_BASE_DIGEST=
ARG GO2RTC_VERSION=v1.9.14
ARG GO2RTC_COMMIT=b5948cfb25404cc5cb37b166ecaa2dca20b11d4b

# Compiled on the build machine for the target, not emulated: go2rtc needs no cgo, so an arm64 image
# builds on an amd64 runner at native speed. By floating tag, like the application's SDK, since the
# toolchain never reaches the image that ships and the newest one carries the newest standard library.
FROM --platform=$BUILDPLATFORM golang:1-alpine AS build
ARG TARGETOS
ARG TARGETARCH
ARG GO2RTC_VERSION
ARG GO2RTC_COMMIT

RUN apk add --no-cache git

WORKDIR /src
RUN git clone --quiet --depth 1 --branch "$GO2RTC_VERSION" https://github.com/AlexxIT/go2rtc.git .
RUN actual="$(git rev-parse HEAD)" && \
    if [ "$actual" != "$GO2RTC_COMMIT" ]; then \
        echo "go2rtc $GO2RTC_VERSION is $actual, not $GO2RTC_COMMIT" >&2; exit 1; \
    fi

# Fixes not in the release, applied after the check so they are applied to exactly that commit. One
# today: stream.mjpeg passes a USB camera's frames on without the Huffman tables the camera leaves
# out, which Safari cannot decode - the stream arrives and nothing is painted. frame.jpeg already
# adds them; the patch makes stream.mjpeg add them too, but only them: frame.jpeg's repair also
# re-encodes a frame whose header it does not know, which on a stream costs a Raspberry Pi 3 a full
# core per viewer. The patch's own message has the measurements.
#
# Every patch by name, so one added to the directory cannot be skipped by accident; git apply fails
# the build on a patch that no longer fits.
COPY patches/0001-Add-missing-Huffman-tables-to-stream.mjpeg-frames.patch /patches/
RUN git apply --verbose /patches/0001-Add-missing-Huffman-tables-to-stream.mjpeg-frames.patch

# The two modules a scanner found fixes for, at their latest - and only those two, because they are
# the Go team's own and keep their API; the rest stay at what the release was tested with. GOTOOLCHAIN
# stops go.mod asking for a toolchain download: this one is newer than the release needs.
#
# HOMESPOOL_PACKAGE_REFRESH reruns this once a day, as it does the apk upgrade below; see there.
ENV GOTOOLCHAIN=local CGO_ENABLED=0
ARG HOMESPOOL_PACKAGE_REFRESH=
RUN go get golang.org/x/crypto@latest golang.org/x/net@latest

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
