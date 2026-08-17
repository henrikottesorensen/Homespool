#!/usr/bin/env bash
#
# Builds the container images with the commit stamped into them.
#
#   ./build.sh                  # both images, as `docker compose build` would
#   ./build.sh homespool        # one service
#   ./build.sh --no-cache       # anything else is passed straight to compose
#
# USE THIS RATHER THAN `docker compose build` if you intend to run, ship or keep the image. Compose
# has no way to compute the commit itself - it does no command substitution - so a bare
# `docker compose build` produces an image whose --version says "commit unknown", and says it only
# when somebody thinks to ask. That is the whole reason this wrapper exists; it is two lines of work
# that compose cannot do.
#
# Nothing else about the build changes, so `docker compose build` remains correct for a throwaway
# image you are about to discard.
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

exec docker compose -f "$repo_root/compose.yaml" build "$@"
