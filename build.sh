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
# It also passes --pull, which is the other thing a bare `docker compose build` does not do. The
# Dockerfiles name their base images by floating tag - aspnet:10.0, nginx-unprivileged:stable - so
# that their publishers' security rebuilds arrive without an edit here. That only works if the build
# asks the registry what the tag points at now. Without --pull the build reuses whatever copy this
# machine pulled first, for as long as it keeps it, and a rebuild months later ships the base image
# it had months ago. The cost is that this needs the registry: offline, `docker compose build` still
# builds from the local copies, with the same "commit unknown" caveat as above.
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

exec docker compose -f "$repo_root/compose.yaml" build --pull "$@"
