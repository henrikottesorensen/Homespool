#!/usr/bin/env bash
#
# Builds the three images for the commit checked out and pushes them to the registry named by
# REGISTRY, then reads back what the registry now holds and checks it is that commit:
#
#   tools/publish-images.sh                       # this machine's platform, under every tag
#   tools/publish-images.sh --platform            # this machine's platform, under <commit>-<arch>
#   tools/publish-images.sh --merge amd64 arm64   # those combined, under every tag
#
# Tags pushed, in this order: the full commit, the release version when HEAD carries a v-tag (see
# tools/release-version.sh), and latest. latest moves last, so a push that fails part-way never
# leaves it pointing at an image the commit's own tag does not have. The full commit rather than an
# abbreviation, because it is exactly the revision label inside the image and cannot collide.
#
# Without an argument the tags hold this machine's platform alone, which is all a registry for one
# kind of machine needs. An image for more than one platform is built on one machine of each kind
# rather than under emulation: --platform on each pushes only <commit>-<arch>, and --merge, run once
# after all of them, builds nothing and makes every tag an index of those.
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
# machine's platform out of whatever the registry answers, a single manifest or an index. --merge
# reads the index from the registry instead, and wants every architecture in it at this commit. The
# digests it prints are what a scan of the published images starts from.
#
# Built through ./build.sh, so every stamp a local build carries - commit, base digests, the daily
# apt refresh, the version - is in what is published. Needs the registry's login already in Docker.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose=(docker compose -f "$repo_root/compose.yaml")
services=(homespool proxy go2rtc)

die() {
    echo "$0: $*" >&2
    exit 1
}

mode=all
architectures=()
case "${1:-}" in
    "") ;;
    --platform) mode=platform ;;
    --merge)
        mode=merge
        shift
        [ $# -gt 0 ] || die "--merge needs the architectures to combine, as in: --merge amd64 arm64"
        architectures=("$@")
        ;;
    *) die "unknown argument: $1" ;;
esac

# An architecture becomes part of a tag and of a --platform value, so it is a plain word or nothing.
for arch in ${architectures[@]+"${architectures[@]}"}; do
    case "$arch" in
        "" | *[!a-z0-9]*) die "'$arch' is not an architecture, such as amd64 or arm64" ;;
    esac
done

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

if [ "$mode" = platform ]; then
    # The daemon's, not the shell's: uname says aarch64 where every image platform says arm64.
    platform_arch="$(docker version --format '{{.Server.Arch}}')"
    export HOMESPOOL_TAG="$commit-$platform_arch"
else
    export HOMESPOOL_TAG="$commit"
fi

images="$("${compose[@]}" config --images "${services[@]}")"

for image in $images; do
    case "$image" in
        */*) ;;
        *) die "REGISTRY is not set, in the environment or .env - refusing to push $image" ;;
    esac
done

# Pulls a tag and fails unless it is this commit.
read_back() {
    local reference="$1" repository="${1%:*}" revision digest

    docker pull --quiet "$reference" >/dev/null

    revision="$(docker image inspect \
        --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$reference")"
    [ "$revision" = "$commit" ] ||
        die "$reference is revision '$revision' in the registry, not $commit"

    digest="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$reference" |
        grep -F "$repository@" | head -n 1)"
    echo "==> $reference  ${digest#"$repository"@}"
}

# Reads an index from the registry, a line per platform, and fails unless every architecture is in
# it at this commit. Not a pull per architecture: a daemon on the containerd image store keeps each
# platform it pulls under the one name and inspects it as its own, so the second pull reads back as
# the first.
read_back_index() {
    local reference="$1" platforms digest arch

    platforms="$(docker buildx imagetools inspect --format \
        '{{range .Image}}{{.Architecture}} {{index .Config.Labels "org.opencontainers.image.revision"}}{{println}}{{end}}' \
        "$reference")"

    for arch in "${architectures[@]}"; do
        printf '%s\n' "$platforms" | grep -q -x -F "$arch $commit" ||
            die "$reference holds no $arch image of $commit; it holds: $(printf '%s' "$platforms" | tr '\n' ',')"
    done

    digest="$(docker buildx imagetools inspect --format '{{.Manifest.Digest}}' "$reference")"
    echo "==> $reference (${architectures[*]})  $digest"
}

case "$mode" in
    platform)
        echo "==> Publishing ${commit} for ${platform_arch}"

        "$repo_root/build.sh"

        "${compose[@]}" push "${services[@]}"

        for image in $images; do
            read_back "$image"
        done
        ;;

    merge)
        echo "==> Combining ${commit} for ${architectures[*]}${version:+ as release $version}"

        for image in $images; do
            repository="${image%:*}"
            sources=()
            for arch in "${architectures[@]}"; do
                sources+=("$repository:$commit-$arch")
            done
            docker buildx imagetools create --tag "$image" "${sources[@]}"
        done

        for image in $images; do
            repository="${image%:*}"

            for tag in ${version:+"$version"} latest; do
                docker buildx imagetools create --tag "$repository:$tag" "$image"
            done
        done

        for image in $images; do
            repository="${image%:*}"

            for tag in "$commit" ${version:+"$version"} latest; do
                read_back_index "$repository:$tag"
            done
        done
        ;;

    all)
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
                read_back "$repository:$tag"
            done
        done
        ;;
esac
