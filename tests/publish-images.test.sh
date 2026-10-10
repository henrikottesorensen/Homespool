#!/usr/bin/env bash
#
# Tests for tools/publish-images.sh - what it refuses to publish, the order it pushes tags in, and
# that it fails when the registry does not hold what it pushed.
#
#   tests/publish-images.test.sh               # run them all
#   tests/publish-images.test.sh refuses       # run only tests whose name contains "refuses"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# Each case builds a scratch git repository holding real copies of the script, tools/gitref.sh and
# tools/release-version.sh, so a modified tree, a v-tag and a commit after one are real git states
# rather than stubbed answers. build.sh and docker are stubs: build.sh records the tag it was asked to
# build, and docker records every call and answers the reads the script makes - the image names, the
# daemon's architecture, each image's revision label and digest, and each index's platforms - from
# the environment.
# Nothing reaches a daemon or a registry.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
filter="${1:-}"

passed=0
failed=0
scratch=""

cleanup() { [ -n "$scratch" ] && rm -rf "$scratch"; }
trap cleanup EXIT

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $1"
    shift
    for line in "$@"; do echo "        $line"; done
}

assert_contains() {
    case "$1" in
        *"$2"*) passed=$((passed + 1)) ;;
        *) fail "$3" "expected to contain: $2" "actual: $1" ;;
    esac
}

assert_not_contains() {
    case "$1" in
        *"$2"*) fail "$3" "expected NOT to contain: $2" "actual: $1" ;;
        *) passed=$((passed + 1)) ;;
    esac
}

assert_status() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "$3" "expected exit $2, got $1"
    fi
}

# Passes when the first line of the log matching $1 comes before the first matching $2.
assert_before() {
    local first second
    first="$(grep -n -F -x -- "$1" "$STUB_LOG" | head -n 1 | cut -d: -f1)"
    second="$(grep -n -F -x -- "$2" "$STUB_LOG" | head -n 1 | cut -d: -f1)"
    if [ -n "$first" ] && [ -n "$second" ] && [ "$first" -lt "$second" ]; then
        passed=$((passed + 1))
    else
        fail "$3" "expected '$1' (line ${first:-none}) before '$2' (line ${second:-none})" \
            "log: $(tr '\n' '|' < "$STUB_LOG")"
    fi
}

git_in() {
    git -C "$scratch/repo" -c core.hooksPath=/dev/null -c commit.gpgsign=false -c tag.gpgsign=false \
        -c user.name=test -c user.email=test@example.net "$@" >/dev/null 2>&1
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    # Through cd and pwd, as the script resolves its own location: a TMPDIR ending in a slash would
    # otherwise leave a // in every path the assertions compare with the script's.
    scratch="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/publish-images.XXXXXX")" && pwd)"
    mkdir -p "$scratch/bin" "$scratch/repo/tools"

    cp "$repo_root/tools/publish-images.sh" "$repo_root/tools/gitref.sh" \
        "$repo_root/tools/release-version.sh" "$scratch/repo/tools/"
    printf '# compose\n' > "$scratch/repo/compose.yaml"

    cat > "$scratch/repo/build.sh" <<'STUB'
#!/bin/sh
printf 'build.sh HOMESPOOL_TAG=%s\n' "${HOMESPOOL_TAG:-}" >> "$STUB_LOG"
STUB
    chmod 755 "$scratch/repo/build.sh"

    cat > "$scratch/bin/docker" <<'STUB'
#!/bin/sh
# Every invocation, flattened, for assertions about what was pushed and in which order.
printf '%s\n' "$*" >> "$STUB_LOG"

case "$1" in
    compose)
        shift
        [ "$1" = -f ] && shift 2
        case "$1" in
            config)
                # config --images <service>...: each service's image, named as compose.yaml names it
                shift 2
                for service in "$@"; do
                    case "$service" in
                        homespool) name=homespool ;;
                        proxy) name=homespool-proxy ;;
                        go2rtc) name=homespool-go2rtc ;;
                        *) echo "no such service: $service" >&2; exit 1 ;;
                    esac
                    printf '%s\n' "${STUB_REGISTRY:+$STUB_REGISTRY/}$name:${HOMESPOOL_TAG:-latest}"
                done
                ;;
            push) [ -n "${STUB_PUSH_FAILS:-}" ] && exit 1 ;;
        esac
        ;;
    version) printf '%s\n' "${STUB_ARCH:-amd64}" ;;
    push) [ -n "${STUB_PUSH_FAILS:-}" ] && exit 1 ;;
    buildx)
        case "$3" in
            create) [ -n "${STUB_PUSH_FAILS:-}" ] && exit 1 ;;
            # imagetools inspect --format <format> <reference>: an index of the platforms named
            inspect)
                # As buildx before 0.33.0 does, which prints its summary for any format starting
                # {{.Manifest rather than executing it.
                case "$5" in
                    '{{.Manifest'*) printf 'Name:      %s\nMediaType: application/vnd.oci.image.index.v1+json\nDigest:    sha256:beadfeed\n' "$6" ;;
                    *Manifest.Digest*) printf 'sha256:beadfeed\n' ;;
                    *)
                        for arch in ${STUB_INDEX_ARCHS-amd64 arm64}; do
                            printf '%s %s\n' "$arch" "${STUB_REVISION:-$STUB_COMMIT}"
                        done
                        ;;
                esac
                ;;
        esac
        ;;
    image)
        # image inspect --format <format> <reference>
        case "$4" in
            *revision*) printf '%s\n' "${STUB_REVISION:-$STUB_COMMIT}" ;;
            *RepoDigests*) printf '%s@sha256:feedface\n' "${5%:*}" ;;
        esac
        ;;
esac
exit 0
STUB
    chmod 755 "$scratch/bin/docker"

    git_in init -q
    git_in add -A
    git_in commit -q -m initial

    export PATH="$scratch/bin:$PATH"
    export STUB_LOG="$scratch/docker.log"
    export STUB_REGISTRY="registry.example.net"
    export STUB_COMMIT
    STUB_COMMIT="$(git -C "$scratch/repo" rev-parse HEAD)"
    unset STUB_REVISION STUB_PUSH_FAILS STUB_ARCH STUB_INDEX_ARCHS
    : > "$STUB_LOG"
    return 0
}

publish() {
    output="$("$scratch/repo/tools/publish-images.sh" "$@" 2>&1)"
    status=$?
    log="$(cat "$STUB_LOG")"
}

if test_case "publishes the commit's own tag, then latest, and reads both back"; then
    publish
    reg="registry.example.net"
    assert_status "$status" 0 "an ordinary commit publishes"
    assert_contains "$log" "build.sh HOMESPOOL_TAG=$STUB_COMMIT" "it builds under the full commit"
    assert_before "compose -f $scratch/repo/compose.yaml push homespool proxy go2rtc" \
        "push $reg/homespool:latest" "the commit's tag goes out before latest moves"
    assert_contains "$log" "tag $reg/homespool-proxy:$STUB_COMMIT $reg/homespool-proxy:latest" \
        "latest is the image just built, not whatever the name held"
    assert_contains "$log" "pull --quiet $reg/homespool:$STUB_COMMIT" "the commit's tag is read back"
    assert_contains "$log" "pull --quiet $reg/homespool-proxy:latest" "latest is read back"
    assert_contains "$log" "tag $reg/homespool-go2rtc:$STUB_COMMIT $reg/homespool-go2rtc:latest" \
        "the camera sidecar is published with the other two"
    assert_contains "$log" "pull --quiet $reg/homespool-go2rtc:latest" "the camera sidecar is read back"
    assert_contains "$output" "==> $reg/homespool:$STUB_COMMIT  sha256:feedface" "it prints each digest"
    assert_not_contains "$output" "release" "no v-tag, no release"
fi

if test_case "a v-tagged commit also pushes the version, before latest"; then
    git_in tag v0.1
    publish
    reg="registry.example.net"
    assert_status "$status" 0 "a release publishes"
    assert_contains "$output" "as release 0.1" "it says which release"
    assert_before "push $reg/homespool:0.1" "push $reg/homespool:latest" "the version before latest"
    assert_contains "$log" "pull --quiet $reg/homespool-proxy:0.1" "the version is read back"
fi

if test_case "a commit after the v-tag is not the release"; then
    git_in tag v0.1
    printf 'more\n' > "$scratch/repo/later.txt"
    git_in add -A
    git_in commit -q -m later
    STUB_COMMIT="$(git -C "$scratch/repo" rev-parse HEAD)"
    publish
    assert_status "$status" 0 "a later commit publishes"
    assert_not_contains "$log" ":0.1" "but not under the release's tag"
fi

if test_case "refuses a modified tree, before building anything"; then
    printf 'edit\n' > "$scratch/repo/untracked.txt"
    publish
    assert_status "$status" 1 "a modified tree is refused"
    assert_contains "$output" "working tree differs from HEAD" "and says why"
    assert_not_contains "$log" "build.sh" "nothing is built"
    assert_not_contains "$log" "push" "nothing is pushed"
fi

if test_case "refuses a v-tagged but modified tree as well"; then
    git_in tag v0.1
    printf 'edit\n' >> "$scratch/repo/compose.yaml"
    publish
    assert_status "$status" 1 "a modified release tree is refused"
    assert_not_contains "$log" "push" "nothing is pushed"
fi

if test_case "refuses without a registry, before building anything"; then
    STUB_REGISTRY=""
    publish
    assert_status "$status" 1 "no registry is refused"
    assert_contains "$output" "REGISTRY is not set" "and says why"
    assert_not_contains "$log" "build.sh" "nothing is built"
    assert_not_contains "$log" "push" "nothing is pushed"
fi

if test_case "refuses a v-tag that is not a valid image tag, before building anything"; then
    git_in tag "v0.1+build.7"
    publish
    assert_status "$status" 1 "a + in the version is refused"
    assert_contains "$output" "not a valid image tag" "and says why"
    assert_not_contains "$log" "build.sh" "nothing is built"
fi

if test_case "a failed push never moves latest"; then
    export STUB_PUSH_FAILS=1
    publish
    assert_status "$status" 1 "the failure stops the script"
    assert_not_contains "$log" ":latest" "latest is neither tagged nor pushed"
fi

if test_case "fails when the registry holds a different revision"; then
    export STUB_REVISION="0000000000000000000000000000000000000000"
    publish
    assert_status "$status" 1 "a mismatch fails the publish"
    assert_contains "$output" "is revision '$STUB_REVISION' in the registry" "and names what it found"
fi

if test_case "--platform pushes this machine's architecture under the commit, and nothing else"; then
    git_in tag v0.1
    export STUB_ARCH=arm64
    publish --platform
    reg="registry.example.net"
    assert_status "$status" 0 "a platform publishes"
    assert_contains "$log" "build.sh HOMESPOOL_TAG=$STUB_COMMIT-arm64" "it builds under the commit and the architecture"
    assert_contains "$log" "compose -f $scratch/repo/compose.yaml push homespool proxy go2rtc" \
        "it pushes all three"
    assert_contains "$log" "pull --quiet $reg/homespool-go2rtc:$STUB_COMMIT-arm64" "and reads each back"
    assert_not_contains "$log" ":latest" "latest does not move"
    assert_not_contains "$log" ":0.1" "nor does the release's tag"
    assert_not_contains "$log" "imagetools" "nothing is combined"
fi

if test_case "--merge combines the architectures under the commit, then the version, then latest"; then
    git_in tag v0.1
    publish --merge amd64 arm64
    reg="registry.example.net"
    assert_status "$status" 0 "a merge publishes"
    assert_contains "$output" "as release 0.1" "it says which release"
    assert_not_contains "$log" "build.sh" "nothing is built"
    assert_contains "$log" \
        "buildx imagetools create --tag $reg/homespool-proxy:$STUB_COMMIT $reg/homespool-proxy:$STUB_COMMIT-amd64 $reg/homespool-proxy:$STUB_COMMIT-arm64" \
        "the commit's tag is an index of every architecture"
    assert_contains "$log" "buildx imagetools create --tag $reg/homespool:latest $reg/homespool:$STUB_COMMIT" \
        "latest is the commit's index"
    assert_before "buildx imagetools create --tag $reg/homespool-go2rtc:$STUB_COMMIT $reg/homespool-go2rtc:$STUB_COMMIT-amd64 $reg/homespool-go2rtc:$STUB_COMMIT-arm64" \
        "buildx imagetools create --tag $reg/homespool:0.1 $reg/homespool:$STUB_COMMIT" \
        "every commit tag goes out before any other"
    assert_before "buildx imagetools create --tag $reg/homespool:0.1 $reg/homespool:$STUB_COMMIT" \
        "buildx imagetools create --tag $reg/homespool:latest $reg/homespool:$STUB_COMMIT" \
        "the version before latest"
    assert_contains "$log" "buildx imagetools inspect --format" "each tag is read back from the registry"
    assert_contains "$output" "==> $reg/homespool:latest (amd64 arm64)  sha256:beadfeed" \
        "latest is read back, with every architecture"
    assert_contains "$output" "==> $reg/homespool-go2rtc:0.1 (amd64 arm64)  sha256:beadfeed" \
        "the version is read back"
    assert_not_contains "$log" "pull" "nothing is pulled"
fi

if test_case "--merge fails when an index lacks an architecture"; then
    export STUB_INDEX_ARCHS=amd64
    publish --merge amd64 arm64
    assert_status "$status" 1 "a missing architecture fails the publish"
    assert_contains "$output" "holds no arm64 image of $STUB_COMMIT; it holds: amd64 $STUB_COMMIT" \
        "and names what it found"
fi

if test_case "--merge fails when an architecture is another revision"; then
    export STUB_REVISION="0000000000000000000000000000000000000000"
    publish --merge amd64 arm64
    assert_status "$status" 1 "a mismatch fails the publish"
    assert_contains "$output" "holds no amd64 image of $STUB_COMMIT" "and says which"
fi

if test_case "--merge's failed push never moves latest"; then
    export STUB_PUSH_FAILS=1
    publish --merge amd64 arm64
    assert_status "$status" 1 "the failure stops the script"
    assert_not_contains "$log" ":latest" "latest is not created"
fi

if test_case "--merge refuses without architectures, or with one no tag can carry"; then
    publish --merge
    assert_status "$status" 1 "no architectures is refused"
    assert_contains "$output" "--merge needs the architectures" "and says why"
    publish --merge amd64 "linux/arm64"
    assert_status "$status" 1 "a platform where an architecture goes is refused"
    assert_contains "$output" "'linux/arm64' is not an architecture" "and says why"
    assert_not_contains "$log" "imagetools" "nothing is combined"
fi

if test_case "refuses a modified tree with --platform and --merge too"; then
    printf 'edit\n' > "$scratch/repo/untracked.txt"
    publish --platform
    assert_status "$status" 1 "--platform refuses a modified tree"
    publish --merge amd64 arm64
    assert_status "$status" 1 "--merge refuses a modified tree"
    assert_not_contains "$log" "build.sh" "nothing is built"
    assert_not_contains "$log" "push" "nothing is pushed"
    assert_not_contains "$log" "imagetools" "nothing is combined"
fi

if test_case "refuses an argument it does not know"; then
    publish --latest
    assert_status "$status" 1 "an unknown argument is refused"
    assert_contains "$output" "unknown argument: --latest" "and says why"
    assert_not_contains "$log" "build.sh" "nothing is built"
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
