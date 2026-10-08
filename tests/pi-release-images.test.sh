#!/usr/bin/env bash
#
# Tests for pi/release-images.sh - what a card built from a release accepts as that release's
# published images, the .env.example it ships, and how the images reach the card's store - and that
# pi/build.sh runs it where it has to.
#
#   tests/pi-release-images.test.sh            # run them all
#   tests/pi-release-images.test.sh refuses    # run only tests whose name contains "refuses"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# docker is a stub: it records every call, answers `buildx imagetools inspect` with a configuration
# per image from the scratch directory, and answers the build daemon's `image inspect` with a
# revision from the environment. jq is real, because reading the registry's answer is jq. Nothing
# reaches a daemon or a registry.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/pi/release-images.sh"
filter="${1:-}"

passed=0
failed=0
scratch=""

registry="ghcr.example.net/someone"
commit="0123456789abcdef0123456789abcdef01234567"
other="fedcba9876543210fedcba9876543210fedcba98"
names="homespool homespool-proxy homespool-go2rtc"

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

assert_equals() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "$3" "expected: $2" "actual: $1"
    fi
}

# Passes when the first line of $3 matching $1 comes before the first matching $2.
assert_before() {
    local first second
    first="$(grep -n -F -- "$1" "$3" | head -n 1 | cut -d: -f1)"
    second="$(grep -n -F -- "$2" "$3" | head -n 1 | cut -d: -f1)"
    if [ -n "$first" ] && [ -n "$second" ] && [ "$first" -lt "$second" ]; then
        passed=$((passed + 1))
    else
        fail "$4" "expected '$1' (line ${first:-none}) before '$2' (line ${second:-none})"
    fi
}

# An image configuration as the registry describes one platform's image.
image_config() {
    printf '{"architecture":"%s","os":"linux","config":{"Labels":{"org.opencontainers.image.revision":"%s"}}}' \
        "$1" "$2"
}

# What `imagetools inspect --format '{{json .Image}}'` answers for an index: one per platform.
publish_index() {
    printf '{"linux/amd64":%s,"linux/arm64":%s}\n' "$(image_config amd64 "$2")" \
        "$(image_config arm64 "$2")" > "$scratch/published/$1"
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/pi-release-images.XXXXXX")" && pwd)"
    mkdir -p "$scratch/bin" "$scratch/published"

    cat > "$scratch/bin/docker" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$STUB_LOG"
case "$1" in
    buildx)
        # buildx imagetools inspect --format <format> <reference>
        reference="$6"
        name="${reference##*/}"
        name="${name%%:*}"
        [ -f "$STUB_PUBLISHED/$name" ] || { echo "$reference: not found" >&2; exit 1; }
        cat "$STUB_PUBLISHED/$name"
        ;;
    exec)
        # exec <container> docker <command> ...
        shift 3
        case "$1" in
            pull) [ -n "${STUB_PULL_FAILS:-}" ] && exit 1 ;;
            image) printf '%s\n' "$STUB_PULLED_REVISION" ;;
        esac
        ;;
esac
exit 0
STUB
    chmod 755 "$scratch/bin/docker"

    export PATH="$scratch/bin:$PATH"
    export STUB_LOG="$scratch/docker.log"
    export STUB_PUBLISHED="$scratch/published"
    export STUB_PULLED_REVISION="$commit"
    unset STUB_PULL_FAILS
    : > "$STUB_LOG"
    for name in $names; do
        publish_index "$name" "$commit"
    done
    return 0
}

run() {
    output="$("$script" "$@" 2>&1)"
    status=$?
    log="$(cat "$STUB_LOG")"
}

# ---- check ----------------------------------------------------------------------------------------

if test_case "check: a release whose three images carry the commit for arm64 passes"; then
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 0 "the release is accepted"
    for name in $names; do
        assert_contains "$log" "buildx imagetools inspect --format {{json .Image}} $registry/$name:0.1" \
            "$name is read under the version's tag"
        assert_contains "$output" "$registry/$name:0.1 is $commit, for linux/arm64" "and said to be the commit"
    done
    assert_not_contains "$log" "pull" "nothing is pulled"
fi

if test_case "check: a single arm64 manifest passes too"; then
    image_config arm64 "$commit" > "$scratch/published/homespool-proxy"
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 0 "a single manifest for arm64 is accepted"
fi

if test_case "check: refuses an image published from another commit"; then
    publish_index homespool-go2rtc "$other"
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 1 "another commit is refused"
    assert_contains "$output" "$registry/homespool-go2rtc:0.1 is revision '$other' in the registry, not $commit" \
        "and names the image and what it found"
fi

if test_case "check: refuses when only another architecture's image carries the commit"; then
    printf '{"linux/amd64":%s,"linux/arm64":%s}\n' "$(image_config amd64 "$commit")" \
        "$(image_config arm64 "$other")" > "$scratch/published/homespool"
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 1 "the arm64 image is the one compared"
    assert_contains "$output" "revision '$other'" "and its revision is the one reported"
fi

if test_case "check: refuses an image without a label"; then
    printf '{"linux/arm64":{"architecture":"arm64","os":"linux","config":{}}}\n' \
        > "$scratch/published/homespool"
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 1 "no revision is not the commit"
    assert_contains "$output" "is revision '' in the registry" "and says it found none"
fi

if test_case "check: refuses a release with no arm64 image"; then
    printf '{"linux/amd64":%s}\n' "$(image_config amd64 "$commit")" > "$scratch/published/homespool-proxy"
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 1 "an index without arm64 is refused"
    assert_contains "$output" "$registry/homespool-proxy:0.1 has no linux/arm64 image" "and says why"
fi

if test_case "check: refuses a single manifest for another architecture"; then
    image_config amd64 "$commit" > "$scratch/published/homespool"
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 1 "an amd64 manifest is refused"
    assert_contains "$output" "has no linux/arm64 image" "and says why"
fi

if test_case "check: refuses an image the registry does not serve"; then
    rm "$scratch/published/homespool-go2rtc"
    run check "$registry" 0.1 "$commit"
    assert_status "$status" 1 "an unreadable image is refused"
    assert_contains "$output" "could not read $registry/homespool-go2rtc:0.1" "and names it"
    assert_not_contains "$output" "is revision" "rather than going on to compare nothing"
fi

# ---- env-example ----------------------------------------------------------------------------------

if test_case "env-example: the repository's .env.example gets the registry, and nothing else changes"; then
    run env-example "$registry" "$repo_root/.env.example" "$scratch/env.example"
    assert_status "$status" 0 "it is written"
    assert_equals "$(grep '^REGISTRY=' "$scratch/env.example")" "REGISTRY=$registry" "REGISTRY names the registry"
    assert_equals "$(diff "$repo_root/.env.example" "$scratch/env.example" | grep -c '^[<>]')" 2 \
        "one line replaced, no other"
    assert_equals "$(grep -c '^HOMESPOOL_TAG=' "$scratch/env.example")" 0 "and no tag is pinned, so latest is followed"
fi

if test_case "env-example: refuses an example whose REGISTRY is already set"; then
    printf 'TZ=UTC\nREGISTRY=elsewhere.example\n' > "$scratch/in"
    run env-example "$registry" "$scratch/in" "$scratch/out"
    assert_status "$status" 1 "a set REGISTRY is refused"
    assert_contains "$output" "exactly one empty REGISTRY= line" "and says why"
fi

if test_case "env-example: refuses an example with no REGISTRY line, or two"; then
    printf 'TZ=UTC\n' > "$scratch/in"
    run env-example "$registry" "$scratch/in" "$scratch/out"
    assert_status "$status" 1 "no REGISTRY line is refused"
    printf 'REGISTRY=\nREGISTRY=\n' > "$scratch/in"
    run env-example "$registry" "$scratch/in" "$scratch/out"
    assert_status "$status" 1 "two are refused"
fi

# ---- pull -----------------------------------------------------------------------------------------

if test_case "pull: each image is pulled for arm64 through the build daemon, then tagged latest"; then
    run pull homespool-dind "$registry" 0.1 "$commit"
    assert_status "$status" 0 "the pull succeeds"
    for name in $names; do
        assert_before "exec homespool-dind docker pull --quiet --platform linux/arm64 $registry/$name:0.1" \
            "exec homespool-dind docker tag $registry/$name:0.1 $registry/$name:latest" "$STUB_LOG" \
            "$name is pulled under the version, then tagged latest"
    done
    assert_not_contains "$log" "load" "nothing is loaded from a tarball"
    assert_not_contains "$log" "save" "or saved to one"
fi

if test_case "pull: refuses an image that pulled as another revision, before tagging it"; then
    export STUB_PULLED_REVISION="$other"
    run pull homespool-dind "$registry" 0.1 "$commit"
    assert_status "$status" 1 "another revision is refused"
    assert_contains "$output" "$registry/homespool:0.1 pulled as revision '$other', not $commit" "and says so"
    assert_not_contains "$log" "docker tag" "nothing is tagged latest"
fi

if test_case "pull: a failed pull fails it"; then
    export STUB_PULL_FAILS=1
    run pull homespool-dind "$registry" 0.1 "$commit"
    assert_status "$status" 1 "the failure stops it"
    assert_contains "$output" "could not pull $registry/homespool:0.1 into the card's store" "and names the image"
    assert_not_contains "$log" "docker tag" "nothing is tagged latest"
fi

if test_case "refuses an unknown mode or the wrong arguments"; then
    run frobnicate
    assert_status "$status" 2 "an unknown mode is a usage error"
    run check "$registry" 0.1
    assert_status "$status" 2 "a missing argument is a usage error"
    assert_not_contains "$log" "imagetools" "and nothing is asked of the registry"
fi

# ---- pi/build.sh ----------------------------------------------------------------------------------

if test_case "wiring: pi/build.sh checks, ships and pulls a release, and builds anything else"; then
    build="$repo_root/pi/build.sh"
    assert_contains "$(cat "$build")" 'release_registry="ghcr.io/henrikottesorensen"' \
        "a release card's registry is GHCR's"
    assert_contains "$(cat "$build")" 'HOMESPOOL_VERSION="$("$repo_root/tools/release-version.sh")"' \
        "a release is what tools/release-version.sh says"
    assert_before '"$pi_dir/release-images.sh" check "$release_registry" "$HOMESPOOL_VERSION" "$release_commit"' \
        'run_imagegen -f' "$build" "the published images are checked before the root filesystem is built"
    # Step 1's branch: from its if to its fi, the check before the else, the build after it.
    awk '/^if \[ -n "\$HOMESPOOL_VERSION" \]; then$/ { on = 1 } on { print } on && /^fi$/ { exit }' \
        "$build" > "$scratch/step1"
    assert_before 'release-images.sh" check' 'else' "$scratch/step1" "a release is checked, not built"
    assert_before 'else' 'compose.yaml" build --pull' "$scratch/step1" "and anything else is built"
    assert_contains "$(cat "$build")" '"$pi_dir/release-images.sh" env-example "$release_registry"' \
        "the card's .env.example names the registry"
    assert_before 'docker run -d --name homespool-dind' '"$pi_dir/release-images.sh" pull homespool-dind' \
        "$build" "the images are pulled by the card's build daemon"
    assert_before '"$pi_dir/release-images.sh" pull homespool-dind' 'docker stop homespool-dind' \
        "$build" "before it stops"
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
