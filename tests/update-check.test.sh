#!/usr/bin/env bash
#
# Tests for update-check/homespool-update-check.sh - what it calls newer, what it says a pull would
# bring, and that a report is never written from part of the evidence.
#
#   tests/update-check.test.sh               # run them all
#   tests/update-check.test.sh runtime       # run only tests whose name contains "runtime"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# The script is RUN, under dash where there is one, exactly as the systemd unit runs it. docker and
# curl are stubs on PATH answering from fixture files a case writes: the running containers and their
# images, what the registry publishes, the published revision's history and Microsoft's release
# metadata. jq is the real one, because the report is jq. Nothing reaches a daemon or the network.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/update-check/homespool-update-check.sh"
filter="${1:-}"

if ! command -v jq >/dev/null 2>&1; then
    echo "SKIP  tests/update-check.test.sh: needs jq, which the script under test needs too."
    exit 0
fi

if command -v dash >/dev/null 2>&1; then
    posix_sh="$(command -v dash)"
else
    posix_sh="/bin/sh"
fi

passed=0
failed=0
scratch=""

running_rev="1111111111111111111111111111111111111111"
published_rev="9999999999999999999999999999999999999999"
source_url="https://github.com/example/homespool"

cleanup() { [ -n "$scratch" ] && rm -rf "$scratch"; }
trap cleanup EXIT

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $1"
    shift
    for line in "$@"; do echo "        $line"; done
}

assert_equals() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "$3" "expected: $2" "actual: $1"
    fi
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
        fail "$3" "expected exit $2, got $1" "stderr: $(cat "$scratch/stderr")"
    fi
}

# running <service> <revision> <source> <base> <aspnet> <repo-digest>
running() {
    jq -n --arg r "$2" --arg s "$3" --arg b "$4" --arg a "$5" --arg d "$6" '{
        RepoDigests: (if $d == "" then [] else [$d] end),
        Config: {
            Labels: {"org.opencontainers.image.revision": $r, "org.opencontainers.image.source": $s,
                     "org.opencontainers.image.base.digest": $b},
            Env: (["PATH=/usr/bin"] + (if $a == "" then [] else ["ASPNET_VERSION=" + $a] end))
        }
    }' > "$scratch/fixtures/running-$1.json"
}

# published <service> <revision> <source> <base> <aspnet>
published() {
    jq -n --arg r "$2" --arg s "$3" --arg b "$4" --arg a "$5" '{
        config: {
            Labels: {"org.opencontainers.image.revision": $r, "org.opencontainers.image.source": $s,
                     "org.opencontainers.image.base.digest": $b},
            Env: (["PATH=/usr/bin"] + (if $a == "" then [] else ["ASPNET_VERSION=" + $a] end))
        }
    }' > "$scratch/fixtures/published-$1.json"
}

# The published revision's history, as GitHub lists it: "sha parent[,parent] subject" per argument.
history() {
    local line sha parents subject out=""
    for line in "$@"; do
        sha="${line%% *}"; line="${line#* }"
        parents="${line%% *}"; subject="${line#* }"
        out="$out${out:+,}$(jq -n --arg sha "$sha" --arg p "$parents" --arg m "$subject" \
            '{sha: $sha, parents: ($p | split(",") | map({sha: .})), commit: {message: ($m + "\n\nbody")}}')"
    done
    printf '[%s]\n' "$out" > "$scratch/fixtures/commits.json"
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/update-check.XXXXXX")" && pwd)"
    mkdir -p "$scratch/bin" "$scratch/fixtures" "$scratch/state"

    cat > "$scratch/bin/docker" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$STUB_LOG"
service_of() { case "$1" in *homespool-proxy*|*proxy*) echo proxy ;; *) echo homespool ;; esac; }
case "$1" in
    version) echo "linux/arm64" ;;
    ps)
        # ps --filter label=...project=<p> --filter label=...service=<s> --format ...
        case "$3" in *"project=${STUB_PROJECT:-homespool}") ;; *) exit 0 ;; esac
        service="${5##*=}"
        eval "ref=\${STUB_REF_$service:-}"
        [ -n "$ref" ] && echo "container-$service"
        ;;
    inspect)
        # inspect --format <format> container-<service>
        service="${4#container-}"
        case "$3" in
            *Config.Image*) eval "printf '%s\n' \"\${STUB_REF_$service}\"" ;;
            *) echo "image-$service" ;;
        esac
        ;;
    buildx)
        # buildx imagetools inspect --format <format> <reference>
        [ -n "${STUB_REGISTRY_FAILS:-}" ] && exit 1
        service="$(service_of "$6")"
        case "$5" in
            *Manifest.Digest*) eval "printf '%s\n' \"\${STUB_DIGEST_$service:-sha256:published-$service}\"" ;;
            *) cat "$STUB_FIXTURES/published-$service.json" ;;
        esac
        ;;
    image)
        # image inspect --format <format> image-<service>
        cat "$STUB_FIXTURES/running-${5#image-}.json"
        ;;
esac
exit 0
STUB

    cat > "$scratch/bin/curl" <<'STUB'
#!/bin/sh
url=""; for a in "$@"; do url="$a"; done
printf 'curl %s\n' "$url" >> "$STUB_LOG"
case "$url" in *"${STUB_CURL_FAILS:-no-such-url}"*) exit 22 ;; esac
case "$url" in
    *releases-index.json) printf '{"releases-index":[{"channel-version":"10.0","releases.json":"https://example.invalid/10.0/releases.json"}]}\n' ;;
    */10.0/releases.json) cat "$STUB_FIXTURES/releases.json" ;;
    https://api.github.com/repos/*/commits*) cat "$STUB_FIXTURES/commits.json" ;;
    *) exit 22 ;;
esac
STUB
    chmod 755 "$scratch/bin/docker" "$scratch/bin/curl"

    # The ordinary starting point: both containers following latest on a registry, both current.
    export STUB_REF_homespool="registry.example.net/homespool"
    export STUB_REF_proxy="registry.example.net/homespool-proxy"
    running homespool "$running_rev" "$source_url" sha256:base-a 10.0.12 \
        "registry.example.net/homespool@sha256:published-homespool"
    running proxy "$running_rev" "$source_url" sha256:base-n "" \
        "registry.example.net/homespool-proxy@sha256:published-proxy"
    published homespool "$running_rev" "$source_url" sha256:base-a 10.0.12
    published proxy "$running_rev" "$source_url" sha256:base-n ""
    history
    printf '{"releases":[]}\n' > "$scratch/fixtures/releases.json"

    export PATH="$scratch/bin:$PATH"
    export STUB_LOG="$scratch/log"
    export STUB_FIXTURES="$scratch/fixtures"
    export STATE_DIRECTORY="$scratch/state"
    unset STUB_PROJECT STUB_REGISTRY_FAILS STUB_CURL_FAILS STUB_DIGEST_homespool STUB_DIGEST_proxy HOMESPOOL_PROJECT
    : > "$STUB_LOG"
    return 0
}

# The published app image moves on from the running one; its fixture decides to what.
newer_app() {
    export STUB_DIGEST_homespool="sha256:newer-homespool"
}

check() {
    output="$("$posix_sh" "$script" 2> "$scratch/stderr")"
    status=$?
    log="$(cat "$STUB_LOG")"
    report="$(cat "$scratch/state/update-check.json" 2>/dev/null)"
}

field() { printf '%s' "$report" | jq -r "$1"; }
app() { field ".services[] | select(.service == \"homespool\") | $1"; }

if test_case "running what the registry serves: current, and nothing is asked of GitHub"; then
    check
    assert_status "$status" 0 "a current deployment checks cleanly"
    assert_equals "$(field .update_available)" "false" "nothing to take"
    assert_equals "$(field '[.services[].status] | join(",")')" "current,current" "both are current"
    assert_equals "$(field .schema)" "1" "the report says which schema it is"
    assert_not_contains "$log" "curl" "no request leaves for a current image"
    assert_contains "$output" "homespool: current" "the journal says so"
fi

if test_case "a newer image with Homespool fixes, counted along the ancestry, not the list order"; then
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    # The published revision merged a branch holding one fix; that fix is OLDER by date than the
    # running revision, so GitHub lists it below it. Counting by position would miss it.
    history \
        "$published_rev m1 fix(Printers): the published one" \
        "m1 $running_rev,f1 Merge branch 'x'" \
        "$running_rev old feat(Files): what is running" \
        "f1 old fix(Host): older by date, newer by ancestry" \
        "old base chore: long ago"
    check
    assert_status "$status" 0 "a newer image checks cleanly"
    assert_equals "$(field .update_available)" "true" "an update is available"
    assert_equals "$(app .status)" "newer" "the app is behind"
    assert_equals "$(app '.homespool.by_type | tojson')" '{"fix":2}' \
        "both fixes counted, the merge and the running commit's ancestry left out"
    assert_contains "$(app '.reasons | join(";")')" "2 Homespool fixes" "and named as a reason"
    assert_contains "$log" "commits?sha=$published_rev" "GitHub is asked about the published revision"
    assert_not_contains "$log" "$running_rev" "and never told the running one"
fi

if test_case "a newer runtime that was a security release is named, releases past the published one are not"; then
    newer_app
    published homespool "$running_rev" "$source_url" sha256:base-b 10.0.13
    printf '%s\n' '{"releases":[
        {"release-version":"10.0.14","security":true,"cve-list":[{"cve-id":"CVE-LATER"}]},
        {"release-version":"10.0.13","security":true,"cve-list":[{"cve-id":"CVE-2026-9999"}]},
        {"release-version":"10.0.12","security":true,"cve-list":[]}]}' > "$scratch/fixtures/releases.json"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_contains "$(app '.reasons | join(";")')" ".NET 10.0.13, a security release" "the runtime release is named"
    assert_equals "$(app '[.runtime.releases[].version] | join(",")')" "10.0.13" \
        "only what the published image carries, not what came after it"
fi

if test_case "the same revision rebuilt is a reason, and GitHub is not asked"; then
    newer_app
    published homespool "$running_rev" "$source_url" sha256:base-b 10.0.12
    check
    assert_status "$status" 0 "checks cleanly"
    assert_contains "$(app '.reasons | join(";")')" "rebuilt from the same revision" "a rebuild is named"
    assert_equals "$(app .base_changed)" "true" "the base change is recorded"
    assert_not_contains "$log" "api.github.com" "no history needed for the same revision"
fi

if test_case "an image from before the labels names nginx's commit, which is not taken for ours"; then
    newer_app
    running homespool "bf9eed6de9a7ff412f1e37d604ee491f2cf78528" \
        "https://github.com/nginx/docker-nginx-unprivileged" "" 10.0.12 ""
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(app .running.revision)" "" "the foreign revision is dropped"
    assert_contains "$(app '.reasons | join(";")')" "does not say which Homespool revision" "and that is said"
    assert_not_contains "$log" "api.github.com" "nothing is asked about somebody else's commit"
    assert_not_contains "$(app '.reasons | join(";")')" "newer base" \
        "and no base is called newer when the running image never said which it was on"
    assert_equals "$(app .base_changed)" "false" "an unknown base is not a changed one"
fi

if test_case "a running revision the fetched history does not reach is said to be beyond it"; then
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    history "$published_rev p1 fix(A): one" "p1 p2 fix(B): two"
    check
    assert_equals "$(app .homespool.found)" "false" "not found in the window"
    assert_contains "$(app '.reasons | join(";")')" "more Homespool changes than the history reaches" \
        "and the count is not passed off as complete"
fi

if test_case "an image built on this machine has nothing to compare with, and nothing is asked"; then
    export STUB_REF_homespool="homespool" STUB_REF_proxy="homespool-proxy:latest"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(field '[.services[].status] | join(",")')" "local,local" "both are local builds"
    assert_not_contains "$log" "imagetools" "no registry is asked"
    assert_not_contains "$log" "curl" "and nothing else either"
fi

if test_case "an image pinned to a digest has no tag to follow"; then
    export STUB_REF_proxy="registry.example.net/homespool-proxy@sha256:abc"
    check
    assert_equals "$(field '.services[] | select(.service == "proxy") | .status')" "pinned" "pinned is reported"
fi

if test_case "a container that is not running is reported, and a project name that matches nothing too"; then
    export STUB_REF_proxy=""
    check
    assert_equals "$(field '.services[] | select(.service == "proxy") | .status')" "not-running" "missing proxy"
    export HOMESPOOL_PROJECT="other"
    check
    assert_equals "$(field '[.services[].status] | join(",")')" "not-running,not-running" \
        "the compose project decides which containers count"
fi

if test_case "an unreachable registry fails the run and leaves the previous report"; then
    printf 'previous\n' > "$scratch/state/update-check.json"
    export STUB_REGISTRY_FAILS=1
    check
    assert_status "$status" 1 "the run fails"
    assert_equals "$report" "previous" "the previous report stands"
    assert_contains "$(cat "$scratch/stderr")" "could not read registry.example.net/homespool:latest" "and says what"
fi

if test_case "an unreachable GitHub fails the run and leaves the previous report"; then
    printf 'previous\n' > "$scratch/state/update-check.json"
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    export STUB_CURL_FAILS="api.github.com"
    check
    assert_status "$status" 1 "the run fails"
    assert_equals "$report" "previous" "the previous report stands"
fi

if test_case "the report is replaced whole, with nothing left beside it"; then
    check
    assert_equals "$(ls -A "$scratch/state" | tr '\n' ' ')" "update-check.json " "only the report is in the directory"
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
