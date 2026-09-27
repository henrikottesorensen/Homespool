#!/usr/bin/env bash
#
# Builds the container images with the commit stamped into them.
#
#   ./build.sh                  # all three images, as `docker compose build` would
#   ./build.sh homespool        # one service
#   ./build.sh --no-cache       # anything else is passed straight to compose
#
# USE THIS RATHER THAN `docker compose build` if you intend to run, ship or keep the image. Compose
# has no way to compute the commit itself - it does no command substitution - so a bare
# `docker compose build` produces an image whose --version says "commit unknown", and says it only
# when somebody thinks to ask. That is the whole reason this wrapper exists; it is a few lines of work
# that compose cannot do.
#
# It also passes --pull, which is the other thing a bare `docker compose build` does not do. The
# Dockerfiles name their base images by floating tag - aspnet:10.0, nginx-unprivileged:stable,
# alpine:3 - so that their publishers' security rebuilds arrive without an edit here. That only
# works if the build asks the registry what the tag points at now. Without --pull the build reuses
# whatever copy this machine pulled first, for as long as it keeps it, and a rebuild months later
# ships the base image it had months ago. The cost is that this needs the registry: offline, `docker
# compose build` still builds from the local copies, with the same "commit unknown" caveat as above.
#
# And it passes the date as HOMESPOOL_APT_REFRESH, so the application image's apt upgrade reruns
# once a day, and the camera sidecar's apk upgrade with it. --pull alone does not do that: an
# unchanged base leaves the upgrade's layer cached, frozen at the day it first ran, which is exactly
# the staleness the upgrade is there to remove. The date rather than something unique per build, so
# rebuilding twice in a day stays cached. Online by the same token as --pull: that layer runs
# apt-get update, and on a machine without the cached layer and without the network the build fails
# there rather than shipping unpatched packages quietly.
#
# And it resolves each image's base tag to a digest first and builds on exactly that, so the image
# can record which base it was built on - see tools/base-digest.sh. The camera sidecar's Go
# toolchain is resolved the same way, and the Go modules it takes at their latest release to exact
# versions (tools/go-module-versions.sh), since both are compiled into the binary it ships. --pull
# stays for the one floating tag left, the SDK the application is compiled in, which never reaches
# the image that ships.
#
# And the release version, for the images' version label, when HEAD carries a v-tag and nothing
# differs from it - tools/release-version.sh. Empty, and the label blank, for every other build.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Empty when there is no repository or no git - a source tarball, say - and the images then report an
# unknown commit rather than failing to build. See tools/gitref.sh.
HOMESPOOL_GITREF="$("$repo_root/tools/gitref.sh")"
export HOMESPOOL_GITREF

if [ -n "$HOMESPOOL_GITREF" ]; then
    echo "==> Stamping the images with ${HOMESPOOL_GITREF}"
else
    echo "==> No commit available, so the images will report an unknown one"
fi

HOMESPOOL_APT_REFRESH="$(date -u +%Y-%m-%d)"
export HOMESPOOL_APT_REFRESH

HOMESPOOL_ASPNET_DIGEST="$("$repo_root/tools/base-digest.sh" "$repo_root/Homespool.Host/Dockerfile")"
HOMESPOOL_NGINX_DIGEST="$("$repo_root/tools/base-digest.sh" "$repo_root/nginx/Dockerfile")"
HOMESPOOL_ALPINE_DIGEST="$("$repo_root/tools/base-digest.sh" "$repo_root/go2rtc/Dockerfile")"
export HOMESPOOL_ASPNET_DIGEST HOMESPOOL_NGINX_DIGEST HOMESPOOL_ALPINE_DIGEST

# The camera sidecar's Go toolchain and the Go modules it takes newer than go2rtc's release, resolved
# the same way and for the same reason: its standard library and those modules are compiled into the
# binary that ships, so the image has to be able to say which ones.
sidecar_dockerfile="$repo_root/go2rtc/Dockerfile"
HOMESPOOL_GOLANG_DIGEST="$("$repo_root/tools/base-digest.sh" "$sidecar_dockerfile" \
    HOMESPOOL_BUILDER_IMAGE)"
HOMESPOOL_GO_MODULES="$("$repo_root/tools/go-module-versions.sh" "$sidecar_dockerfile")"
export HOMESPOOL_GOLANG_DIGEST HOMESPOOL_GO_MODULES

HOMESPOOL_VERSION="$("$repo_root/tools/release-version.sh")"
export HOMESPOOL_VERSION
if [ -n "$HOMESPOOL_VERSION" ]; then
    echo "==> Labelling the images as release ${HOMESPOOL_VERSION}"
fi

echo "==> Building on ${HOMESPOOL_ASPNET_DIGEST} (application), ${HOMESPOOL_NGINX_DIGEST} (proxy)"
echo "    and ${HOMESPOOL_ALPINE_DIGEST} (camera sidecar), compiled on ${HOMESPOOL_GOLANG_DIGEST}"
echo "    with ${HOMESPOOL_GO_MODULES}"

exec docker compose -f "$repo_root/compose.yaml" build --pull "$@"
