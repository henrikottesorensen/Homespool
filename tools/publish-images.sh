#!/usr/bin/env bash
#
# Builds both images for the commit checked out and pushes them to the registry named by REGISTRY,
# then reads back what the registry now holds and checks it is that commit:
#
#   tools/publish-images.sh
#
# Tags pushed, in this order: the full commit, the release version when HEAD carries a v-tag (see
# tools/release-version.sh), and latest. latest moves last, so a push that fails part-way never
# leaves it pointing at an image the commit's own tag does not have. The full commit rather than an
# abbreviation, because it is exactly the revision label inside the image and cannot collide.
#
# REFUSES A MODIFIED TREE. A published image is somebody's deployment, and it has to be a commit
# anybody can check out; tools/gitref.sh's .dirty marker is the test.
#
# REFUSES WITHOUT A REGISTRY, rather than push `homespool` to Docker Hub's library namespace. The
# image names are asked of compose itself, so REGISTRY counts whether it comes from the environment
# or from .env, and nothing here has to parse .env.
#
# The read-back pulls each tag it pushed and compares the image's revision label with the commit. A
# pull, rather than inspecting the registry's manifest directly, because it lets Docker pick this
# machine's platform out of whatever the registry answers, a single manifest or an index. The digests
# it prints are what a scan of the published images starts from.
#
# Built through ./build.sh, so every stamp a local build carries - commit, base digests, the daily
# apt refresh, the version - is in what is published. Needs the registry's login already in Docker.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose=(docker compose -f "$repo_root/compose.yaml")
services=(homespool proxy)

die() {
    echo "$0: $*" >&2
    exit 1
}

commit="$("$repo_root/tools/gitref.sh")"
[ -n "$commit" ] || die "there is no commit to publish - this is not a git checkout"

case "$commit" in
    *.dirty) die "the working tree differs from HEAD; publish a commit, not a working tree" ;;
esac

version="$("$repo_root/tools/release-version.sh")"

# Checked before anything is built or pushed: a tag docker would reject would otherwise fail after
# the commit's own tag had already gone out.
case "$version" in
    *[!A-Za-z0-9_.-]*) die "release version '$version' is not a valid image tag" ;;
esac

export HOMESPOOL_TAG="$commit"

images="$("${compose[@]}" config --images "${services[@]}")"

for image in $images; do
    case "$image" in
        */*) ;;
        *) die "REGISTRY is not set, in the environment or .env - refusing to push $image" ;;
    esac
done

echo "==> Publishing ${commit}${version:+ as release $version}"

"$repo_root/build.sh"

"${compose[@]}" push "${services[@]}"

for image in $images; do
    repository="${image%:*}"

    for tag in ${version:+"$version"} latest; do
        docker tag "$image" "$repository:$tag"
        docker push "$repository:$tag"
    done
done

for image in $images; do
    repository="${image%:*}"

    for tag in "$commit" ${version:+"$version"} latest; do
        reference="$repository:$tag"

        docker pull --quiet "$reference" >/dev/null

        revision="$(docker image inspect \
            --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$reference")"
        [ "$revision" = "$commit" ] ||
            die "$reference is revision '$revision' in the registry, not $commit"

        digest="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$reference" |
            grep -F "$repository@" | head -n 1)"
        echo "==> $reference  ${digest#"$repository"@}"
    done
done
