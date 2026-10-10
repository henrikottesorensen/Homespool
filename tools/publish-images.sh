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
# LATEST ONLY MOVES FORWARD. Every deployment following latest is told a different image there is an
# update, so latest moves only when the image it holds is no release, or a release no newer than this
# one; see latest_moves. A patch on an older line, or an older release published again, gets its own
# tags and leaves latest where it is. A release version is therefore numbers separated by dots, the
# one order nobody has to write down.
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
# apt refresh, the version - is in what is published. Needs the registry's login already in Docker,
# docker buildx, and jq to read which release latest holds.
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

# A release version is numbers separated by dots: 0.1, 0.1.1, 1.0.
is_release_version() {
    [[ "$1" =~ ^[0-9]+(\.[0-9]+)*$ ]]
}

# Succeeds when release version $1 is lower than $2, compared number by number: 0.10 is above 0.9,
# and 0.1 is the same release as 0.1.0.
version_lower() {
    local a b i x y
    IFS=. read -r -a a <<< "$1"
    IFS=. read -r -a b <<< "$2"
    for ((i = 0; i < ${#a[@]} || i < ${#b[@]}; i++)); do
        x=$((10#${a[i]:-0}))
        y=$((10#${b[i]:-0}))
        [ "$x" -lt "$y" ] && return 0
        [ "$x" -gt "$y" ] && return 1
    done
    return 1
}

# Checked before anything is built or pushed: a version latest cannot be ordered by would otherwise
# fail after the commit's own tag had already gone out. Every such version is a valid image tag too.
if [ -n "$version" ] && ! is_release_version "$version"; then
    die "release version '$version' is not numbers separated by dots, such as 0.1 or 1.2.3"
fi

if [ "$mode" != platform ]; then
    command -v jq >/dev/null 2>&1 || die "jq is needed to read which release latest holds, and is not on PATH"
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

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

# Whether latest may move to this build in repository $1: it may unless the image latest holds there
# is a release, and this build is an older release or none. Read from the registry rather than from
# git, so whatever order releases are published in - a patch on an older line after a newer release,
# an older release's jobs run again - latest only moves forward. A repository with no latest yet, or
# one holding an image published from a commit rather than a release, takes this build.
#
# Each repository answers for itself, so a run that stopped part-way is finished by the next one
# rather than leaving the three latest tags on different releases. Not found is the one failure that
# means there is no latest: any other, and the run stops before latest moves anywhere.
#
# Every failure is its own die: called in a condition, nothing here stops on set -e.
latest_moves() {
    local reference="$1:latest" held

    if ! docker buildx imagetools inspect --format '{{json .Image}}' "$reference" \
        > "$work/latest.json" 2> "$work/latest.err"; then
        grep -q -x -F "ERROR: $reference: not found" "$work/latest.err" && return 0
        die "could not read which release $reference holds: $(cat "$work/latest.err")"
    fi

    # One platform's configuration: a single manifest answers with it directly, an index with one per
    # platform, and every platform of one publish carries the same version.
    held="$(jq -r 'if has("config") then . else (to_entries[0].value) end
        | .config.Labels["org.opencontainers.image.version"] // ""' "$work/latest.json")" ||
        die "could not read the version of the image $reference holds"

    [ -n "$held" ] || return 0
    is_release_version "$held" ||
        die "$reference holds release '$held', which cannot be put in order with this one; point latest at a release by hand"

    if [ -z "$version" ]; then
        echo "==> $reference stays at release $held: $commit is no release"
        return 1
    fi
    if version_lower "$version" "$held"; then
        echo "==> $reference stays at release $held, newer than $version"
        return 1
    fi
    return 0
}

# The tags to move in repository $1 after the commit's own, a line each: the version, and latest
# unless the repository is one of $kept.
moving_tags() {
    [ -z "$version" ] || printf '%s\n' "$version"
    case "$kept" in
        *" $1 "*) ;;
        *) echo latest ;;
    esac
}

# Decides latest in every repository before any tag moves, recording in $kept those it stays in.
decide_latest() {
    local image
    kept=" "
    for image in $images; do
        latest_moves "${image%:*}" || kept="$kept${image%:*} "
    done
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

    # Not piped: grep -q stops at the first match, and the printf it leaves writing dies of SIGPIPE,
    # which pipefail reports as no match.
    for arch in "${architectures[@]}"; do
        grep -q -x -F "$arch $commit" <<< "$platforms" ||
            die "$reference holds no $arch image of $commit; it holds: $(printf '%s' "$platforms" | tr '\n' ',')"
    done

    # `print`, not a bare {{.Manifest.Digest}}: buildx before 0.33.0 answers any format starting
    # {{.Manifest with its human-readable summary instead, and Debian and Ubuntu still ship one.
    digest="$(docker buildx imagetools inspect --format '{{print .Manifest.Digest}}' "$reference")"
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

        decide_latest

        for image in $images; do
            repository="${image%:*}"

            for tag in $(moving_tags "$repository"); do
                docker buildx imagetools create --tag "$repository:$tag" "$image"
            done
        done

        for image in $images; do
            repository="${image%:*}"

            for tag in "$commit" $(moving_tags "$repository"); do
                read_back_index "$repository:$tag"
            done
        done
        ;;

    all)
        echo "==> Publishing ${commit}${version:+ as release $version}"

        "$repo_root/build.sh"

        "${compose[@]}" push "${services[@]}"

        decide_latest

        for image in $images; do
            repository="${image%:*}"

            for tag in $(moving_tags "$repository"); do
                docker tag "$image" "$repository:$tag"
                docker push "$repository:$tag"
            done
        done

        for image in $images; do
            repository="${image%:*}"

            for tag in "$commit" $(moving_tags "$repository"); do
                read_back "$repository:$tag"
            done
        done
        ;;
esac
