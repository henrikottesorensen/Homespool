#!/usr/bin/env bash
#
# Prints the digest a Dockerfile's base image tag points at right now, and nothing else:
#
#   tools/base-digest.sh Homespool.Host/Dockerfile
#   sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f
#
# The Dockerfiles name their base by floating tag, so that the publishers' rebuilds arrive without an
# edit here - and a build on a floating tag cannot say afterwards which image it was built on. So the
# build scripts resolve the tag first and pass the digest in: the image is built on exactly that one
# and records it, which is what lets anything later ask whether the base has been republished since.
#
# The tag is read from the Dockerfile's own `ARG HOMESPOOL_BASE_IMAGE=` line rather than written
# here as well, so the Dockerfile stays the one place a base image is named. A second argument names
# another ARG instead, for an image a build stage runs on that is not the base - the camera sidecar's
# Go toolchain, whose standard library is compiled into what ships:
#
#   tools/base-digest.sh go2rtc/Dockerfile HOMESPOOL_BUILDER_IMAGE
#
# The digest is the tag's index - the multi-platform list - rather than one platform's manifest,
# because that is what a registry answers for the tag, and so what a later comparison will see.
#
# FAILS, unlike tools/gitref.sh, when the tag cannot be resolved: an empty answer here would build on
# the floating tag while claiming nothing, which is the state this exists to end. It needs the
# registry, as the --pull beside it already does.
set -euo pipefail

dockerfile="${1:?usage: $0 <Dockerfile> [ARG name]}"
arg="${2:-HOMESPOOL_BASE_IMAGE}"

image="$(sed -n "s/^ARG ${arg}=//p" "$dockerfile")"

if [ -z "$image" ]; then
    echo "$0: no 'ARG ${arg}=' line in $dockerfile" >&2
    exit 1
fi

# `print`, not a bare {{.Manifest.Digest}}: buildx before 0.33.0 answers any format starting
# {{.Manifest with its human-readable summary instead, and Debian and Ubuntu still ship one.
digest="$(docker buildx imagetools inspect --format '{{print .Manifest.Digest}}' "$image")"

case "$digest" in
    sha256:*) printf '%s\n' "$digest" ;;
    *)
        echo "$0: could not resolve $image to a digest (got '$digest')" >&2
        exit 1
        ;;
esac
