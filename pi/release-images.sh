#!/usr/bin/env bash
#
# The published images a card built from a release carries, instead of images built on the machine
# that made it. pi/build.sh runs it in three steps:
#
#   pi/release-images.sh check <registry> <version> <commit>
#   pi/release-images.sh env-example <registry> <.env.example> <out>
#   pi/release-images.sh pull <container> <registry> <version> <commit>
#
# check - before the card is built: each of the three images is published under the release's
#   version with a linux/arm64 image whose revision label is the commit checked out. Read from the
#   registry, nothing pulled, so a release that was never published, is still private, or was
#   published from another commit fails in seconds rather than after the root filesystem.
# env-example - the card's .env.example with REGISTRY set to the registry. First boot creates the
#   card's .env from it, so the stack asks for the registry's names, follows latest, and the daily
#   update check compares it with what the registry publishes.
# pull - into the card's Docker store, through the build daemon running in <container>: each image
#   pulled for linux/arm64 under the version, its revision checked again, and tagged latest, the tag
#   the card's compose.yaml asks for when .env pins none.
#
# PULLED BY THE DAEMON THAT WRITES THE CARD'S STORE, NOT SAVED AND LOADED. A pulled image records
# the registry's digest for it, and that digest is how the update check knows the running image is
# the published one; `docker load` brings the image without it, and the card would then report its
# own release as built from source, with nothing to compare.
#
# Under the version rather than the commit, which is the tag a rebuild of the release would move;
# the revision check is what ties it to the commit. Needs docker with buildx, and jq.
set -euo pipefail

names=(homespool homespool-proxy homespool-go2rtc)
revision_label="org.opencontainers.image.revision"

die() {
    echo "$0: $*" >&2
    exit 1
}

usage() {
    sed -n '7,9p' "${BASH_SOURCE[0]}" | sed 's/^#  *//' >&2
    exit 2
}

check() {
    [ $# -eq 3 ] || usage
    local registry="$1" version="$2" commit="$3" name reference published config revision
    command -v jq >/dev/null 2>&1 || die "needs jq, to read the registry's answer"

    for name in "${names[@]}"; do
        reference="$registry/$name:$version"
        published="$(docker buildx imagetools inspect --format '{{json .Image}}' "$reference")" ||
            die "could not read $reference - is the release published, and its package public?"

        # A single manifest answers with its one configuration, an index with one per platform.
        config="$(jq -c '
            def arm64: .os == "linux" and .architecture == "arm64";
            if has("config") then (if arm64 then . else null end)
            else [to_entries[] | select(.key | test("^linux/arm64(/v8)?$")) | .value][0]
            end' <<< "$published")" ||
            die "could not read $reference's configuration"
        case "$config" in
            "" | null) die "$reference has no linux/arm64 image" ;;
        esac
        revision="$(jq -r --arg l "$revision_label" '.config.Labels[$l] // ""' <<< "$config")"
        [ "$revision" = "$commit" ] ||
            die "$reference is revision '$revision' in the registry, not $commit"
        echo "    $reference is $commit, for linux/arm64"
    done
}

env_example() {
    [ $# -eq 3 ] || usage
    local registry="$1" example="$2" out="$3"
    # Exactly one empty REGISTRY= line, or what the card would boot with is not what this says.
    if [ "$(grep -c '^REGISTRY=' "$example")" != 1 ] || ! grep -qx 'REGISTRY=' "$example"; then
        die "$example does not have exactly one empty REGISTRY= line to set"
    fi
    sed "s|^REGISTRY=\$|REGISTRY=$registry|" "$example" > "$out"
    grep -qx "REGISTRY=$registry" "$out" || die "REGISTRY was not set in $out"
}

pull() {
    [ $# -eq 4 ] || usage
    local container="$1" registry="$2" version="$3" commit="$4" name reference revision

    for name in "${names[@]}"; do
        reference="$registry/$name:$version"
        docker exec "$container" docker pull --quiet --platform linux/arm64 "$reference" \
            >/dev/null || die "could not pull $reference into the card's store"
        revision="$(docker exec "$container" docker image inspect \
            --format "{{index .Config.Labels \"$revision_label\"}}" "$reference")" ||
            die "could not inspect $reference in the card's store"
        [ "$revision" = "$commit" ] ||
            die "$reference pulled as revision '$revision', not $commit"
        docker exec "$container" docker tag "$reference" "$registry/$name:latest" ||
            die "could not tag $reference as latest in the card's store"
    done
}

[ $# -ge 1 ] || usage
mode="$1"
shift
case "$mode" in
    check) check "$@" ;;
    env-example) env_example "$@" ;;
    pull) pull "$@" ;;
    *) usage ;;
esac
