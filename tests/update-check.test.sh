#!/usr/bin/env bash
#
# Tests for update-check/homespool-update-check.sh - what it calls newer, what it says a pull would
# bring, that a report is never written from part of the evidence, and that the watch starts a check
# when, and only when, the running images changed.
#
#   tests/update-check.test.sh               # run them all
#   tests/update-check.test.sh runtime       # run only tests whose name contains "runtime"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# The script is RUN, under dash where there is one, exactly as the systemd unit runs it. docker and
# curl are stubs on PATH answering from fixture files a case writes: the running containers and their
# images, what the registry publishes, the published revision's history and Microsoft's release
# metadata. systemctl is a stub that only records. jq is the real one, because the report is jq.
# Nothing reaches a daemon or the network.
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
        Created: "2026-08-14T02:34:59.915970875Z",
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

# A history as GitHub lists it, into a fixture file: "sha parent[,parent] subject" per argument.
history_to() {
    local file="$1" line sha parents subject out=""
    shift
    for line in "$@"; do
        sha="${line%% *}"; line="${line#* }"
        parents="${line%% *}"; subject="${line#* }"
        out="$out${out:+,}$(jq -n --arg sha "$sha" --arg p "$parents" --arg m "$subject" \
            '{sha: $sha, parents: ($p | split(",") | map({sha: .})), commit: {message: ($m + "\n\nbody")}}')"
    done
    printf '[%s]\n' "$out" > "$scratch/fixtures/$file"
}

# The published revision's history, its first page.
history() { history_to commits.json "$@"; }

# The published revision's history for one path, its first page.
path_history() {
    local path="$1"
    shift
    history_to "commits-$path.json" "$@"
}

# More labels on a running or published image: labels <running|published> <service> <json object>.
labels() {
    local file="$scratch/fixtures/$1-$2.json"
    jq --argjson l "$3" 'if has("config") then .config.Labels += $l else .Config.Labels += $l end' "$file" > "$file.new" &&
        mv "$file.new" "$file"
}

# The labels a build through ./build.sh sets for what the image is built from.
ns="io.github.henrikottesorensen.homespool"
built_from() {
    jq -n --arg ns "$ns" --arg p "$1" --arg t "$2" --arg r "${3:-2026-10-07}" \
        '{"\($ns).context.path": $p, "\($ns).context.tree": $t, "\($ns).packages.refreshed": $r}'
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
service_of() { case "$1" in *go2rtc*) echo go2rtc ;; *proxy*) echo proxy ;; *) echo homespool ;; esac; }
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
            *Mounts*) printf '%s\n' "${STUB_REPORT_VOLUME:-}" ;;
            *) eval "printf '%s\n' \"\${STUB_IMAGE_$service:-image-$service}\"" ;;
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
        # image inspect --format <format> <an image id naming its service>
        cat "$STUB_FIXTURES/running-$(service_of "$5").json"
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
    https://api.github.com/repos/*/commits*)
        # commits.json for the first page of the whole history, commits-<path>.json for a path's,
        # with .<page> before .json past the first. A page nobody wrote is past the end: empty.
        path="$(printf '%s' "$url" | sed -n 's/.*[?&]path=\([^&]*\).*/\1/p')"
        page="$(printf '%s' "$url" | sed -n 's/.*[?&]page=\([0-9]*\).*/\1/p')"
        file="$STUB_FIXTURES/commits${path:+-$path}$([ "${page:-1}" = 1 ] || printf '.%s' "$page").json"
        if [ -f "$file" ]; then cat "$file"; else echo '[]'; fi
        ;;
    *) exit 22 ;;
esac
STUB
    # setpriv is Linux's; here it records how it was asked to drop privileges, and runs the command.
    cat > "$scratch/bin/setpriv" <<'STUB'
#!/bin/sh
printf 'setpriv %s\n' "$*" >> "$STUB_LOG"
while [ $# -gt 0 ]; do case "$1" in --*) shift ;; *) break ;; esac; done
exec "$@"
STUB
    cat > "$scratch/bin/systemctl" <<'STUB'
#!/bin/sh
printf 'systemctl %s\n' "$*" >> "$STUB_LOG"
STUB
    chmod 755 "$scratch/bin/docker" "$scratch/bin/curl" "$scratch/bin/setpriv" "$scratch/bin/systemctl"

    # The ordinary starting point: all three containers following latest on a registry, all current.
    export STUB_REF_homespool="registry.example.net/homespool"
    export STUB_REF_proxy="registry.example.net/homespool-proxy"
    export STUB_REF_go2rtc="registry.example.net/homespool-go2rtc"
    running homespool "$running_rev" "$source_url" sha256:base-a 10.0.12 \
        "registry.example.net/homespool@sha256:published-homespool"
    running proxy "$running_rev" "$source_url" sha256:base-n "" \
        "registry.example.net/homespool-proxy@sha256:published-proxy"
    published homespool "$running_rev" "$source_url" sha256:base-a 10.0.12
    running go2rtc "$running_rev" "$source_url" sha256:base-g "" \
        "registry.example.net/homespool-go2rtc@sha256:published-go2rtc"
    published proxy "$running_rev" "$source_url" sha256:base-n ""
    published go2rtc "$running_rev" "$source_url" sha256:base-g ""
    history
    printf '{"releases":[]}\n' > "$scratch/fixtures/releases.json"

    export PATH="$scratch/bin:$PATH"
    export STUB_LOG="$scratch/log"
    export STUB_FIXTURES="$scratch/fixtures"
    export STATE_DIRECTORY="$scratch/state"
    export RUNTIME_DIRECTORY="$scratch/run"
    export HOMESPOOL_WATCH_DIRECTORY="$scratch/watch"
    unset STUB_PROJECT STUB_REGISTRY_FAILS STUB_CURL_FAILS STUB_DIGEST_homespool STUB_DIGEST_proxy STUB_DIGEST_go2rtc \
        HOMESPOOL_PROJECT \
        STUB_REPORT_VOLUME STUB_IMAGE_homespool STUB_IMAGE_proxy STUB_IMAGE_go2rtc
    : > "$STUB_LOG"
    return 0
}

# The published app image moves on from the running one; its fixture decides to what.
newer_app() {
    export STUB_DIGEST_homespool="sha256:newer-homespool"
}

# The three steps in the unit's order, each marked in the log, stopping at the first that fails - as
# systemd does, which runs ExecStartPost only when ExecStart succeeded.
check() {
    : > "$scratch/stderr"
    output="$(
        { echo "== collect" >> "$STUB_LOG"; "$posix_sh" "$script" collect; } 2>> "$scratch/stderr" &&
        { echo "== compare" >> "$STUB_LOG"; "$posix_sh" "$script" compare; } 2>> "$scratch/stderr" &&
        { echo "== publish" >> "$STUB_LOG"; "$posix_sh" "$script" publish; } 2>> "$scratch/stderr"
    )"
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
    assert_equals "$(field '[.services[].status] | join(",")')" "current,current,current" "all three are current"
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
    assert_equals "$(app '.reasons | join(";")')" \
        "at least 2 Homespool fixes (the running revision is older than the 2 commits read)" \
        "and the count is said to be a floor, in one reason"
fi

if test_case "a running revision beyond the history with no fix in it is said to be unknown, not fine"; then
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    history "$published_rev p1 feat(A): one"
    check
    assert_equals "$(app '.reasons | join(";")')" \
        "a running revision older than the 1 commits read, so what changed since is not known" \
        "nothing counted is not nothing changed"
fi

if test_case "one fix is one fix"; then
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    history "$published_rev $running_rev fix(A): one" "$running_rev old feat(B): two"
    check
    assert_equals "$(app '.reasons | join(";")')" "1 Homespool fix" "singular"
fi

if test_case "the history is read a page at a time until it reaches the running revision"; then
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    # A first page of 100 commits in a line, each a fix, the running revision on the second.
    jq -n --arg top "$published_rev" '[range(100)] | map({
            sha: (if . == 0 then $top else "c\(.)" end), parents: [{sha: "c\(. + 1)"}],
            commit: {message: "fix(A): \(.)"}})' > "$scratch/fixtures/commits.json"
    history_to commits.2.json "c100 $running_rev fix(B): the last one" "$running_rev old feat(C): running"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(app .homespool.found)" "true" "found on the second page"
    assert_equals "$(app .homespool.read)" "102" "both pages read"
    assert_equals "$(app '.reasons | join(";")')" "101 Homespool fixes" "every one counted"
    assert_equals "$(grep -c "commits?sha=$published_rev&per_page" "$STUB_LOG")" "2" "two requests, not more"
    assert_not_contains "$log" "$running_rev" "and still never told the running revision"
fi

if test_case "the history is read no further than the page limit"; then
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    for page in 1 2 3 4 5 6; do
        jq -n --argjson p "$page" --arg top "$published_rev" '[range(100)] | map(. + ($p - 1) * 100) | map({
                sha: (if . == 0 then $top else "c\(.)" end), parents: [{sha: "c\(. + 1)"}],
                commit: {message: "feat(A): \(.)"}})' > "$scratch/fixtures/commits.$page.json"
    done
    mv "$scratch/fixtures/commits.1.json" "$scratch/fixtures/commits.json"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(grep -c "commits?sha=$published_rev&per_page" "$STUB_LOG")" "5" "five pages, the sixth not asked for"
    assert_equals "$(app .homespool.found)" "false" "the running revision is not in them"
    assert_equals "$(app .homespool.read)" "500" "and says how much it read"
fi

if test_case "each image counts the commits that touch what it is built from"; then
    newer_app
    export STUB_DIGEST_go2rtc="sha256:newer-go2rtc" STUB_DIGEST_proxy="sha256:newer-proxy"
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    published go2rtc "$published_rev" "$source_url" sha256:base-g ""
    published proxy "$published_rev" "$source_url" sha256:base-n ""
    labels published homespool "$(built_from . tree-a2)"
    labels published go2rtc "$(built_from go2rtc tree-g2)"
    labels published proxy "$(built_from nginx tree-n2)"
    path_history go2rtc \
        "f3 f2 fix(Cameras): the sidecar's" \
        "f1 $running_rev fix(Cameras): also the sidecar's, and the application's" \
        "f3x old fix(Cameras): a fix the running revision already has"
    path_history nginx "f2 f1 fix(Proxy): the proxy's"
    # The running revision already has f3x: it is in the path's history and not among the missing.
    history \
        "$published_rev f3 fix(Printers): the application's" \
        "f3 f2 fix(Cameras): the sidecar's" \
        "f2 f1 fix(Proxy): the proxy's" \
        "f1 $running_rev fix(Cameras): also the sidecar's, and the application's" \
        "$running_rev f3x feat(Files): what is running" \
        "f3x old fix(Cameras): a fix the running revision already has"
    check
    assert_status "$status" 0 "checks cleanly"
    service_reasons() { field ".services[] | select(.service == \"$1\") | .reasons | join(\";\")"; }
    assert_equals "$(service_reasons homespool)" "4 Homespool fixes" "the application: the whole repository"
    assert_equals "$(service_reasons go2rtc)" "2 Homespool fixes" "the sidecar: go2rtc/ alone"
    assert_equals "$(service_reasons proxy)" "1 Homespool fix" "the proxy: nginx/ alone"
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .homespool.path')" "go2rtc" \
        "the report says what was counted"
    assert_contains "$log" "commits?sha=$published_rev&path=go2rtc" "the path is asked of GitHub"
    assert_equals "$(grep -c "commits?sha=$published_rev&per_page" "$STUB_LOG")" "1" \
        "and the whole history once, shared by all three"
    assert_not_contains "$log" "$running_rev" "the running revision is still never sent"
fi

if test_case "a context path that is not a plain path is not put into a URL"; then
    export STUB_DIGEST_go2rtc="sha256:newer-go2rtc"
    published go2rtc "$published_rev" "$source_url" sha256:base-g ""
    labels published go2rtc "$(built_from 'go2rtc&sha=elsewhere' tree-g2)"
    history "$published_rev $running_rev fix(A): one" "$running_rev old feat(B): two"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_not_contains "$log" "elsewhere" "the label is not sent"
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .homespool.path')" "." \
        "the whole repository is counted instead"
fi

if test_case "an image of the same source stamped with another commit is restamped, not newer"; then
    export STUB_DIGEST_proxy="sha256:newer-proxy"
    published proxy "$published_rev" "$source_url" sha256:base-n ""
    labels running proxy "$(built_from nginx tree-n)"
    labels published proxy "$(built_from nginx tree-n)"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(field '.services[] | select(.service == "proxy") | .status')" "restamped" "restamped"
    assert_equals "$(field '.services[] | select(.service == "proxy") | .published.revision')" "$published_rev" \
        "with both revisions"
    assert_equals "$(field .update_available)" "false" "nothing to take"
    assert_not_contains "$log" "api.github.com" "and nothing asked about history"
    assert_contains "$output" "proxy: the published image is this one stamped with another commit" "the journal says so"
fi

if test_case "the same source on another base is newer, and says so"; then
    export STUB_DIGEST_proxy="sha256:newer-proxy"
    published proxy "$published_rev" "$source_url" sha256:base-n2 ""
    labels running proxy "$(built_from nginx tree-n)"
    labels published proxy "$(built_from nginx tree-n)"
    check
    assert_equals "$(field '.services[] | select(.service == "proxy") | .status')" "newer" "newer"
    assert_equals "$(field '.services[] | select(.service == "proxy") | .reasons | join(";")')" "built on a newer base" \
        "for the base alone"
    assert_not_contains "$log" "api.github.com" "its own source did not change, so no history is read"
fi

if test_case "the same source with packages refreshed on another day is newer, with no reason"; then
    export STUB_DIGEST_go2rtc="sha256:newer-go2rtc"
    published go2rtc "$published_rev" "$source_url" sha256:base-g ""
    labels running go2rtc "$(built_from go2rtc tree-g 2026-10-04)"
    labels published go2rtc "$(built_from go2rtc tree-g 2026-10-07)"
    check
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .status')" "newer" "newer"
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .reasons | length')" "0" "with no reason"
fi

if test_case "an image with no tree is never called restamped"; then
    export STUB_DIGEST_proxy="sha256:newer-proxy"
    published proxy "$published_rev" "$source_url" sha256:base-n ""
    labels running proxy "$(built_from nginx '')"
    labels published proxy "$(built_from nginx '')"
    history "$published_rev $running_rev feat(A): one" "$running_rev old feat(B): two"
    check
    assert_equals "$(field '.services[] | select(.service == "proxy") | .status')" "newer" \
        "an unknown tree is not an equal one"
fi

if test_case "newer Go modules are a reason, and named"; then
    export STUB_DIGEST_go2rtc="sha256:newer-go2rtc"
    published go2rtc "$published_rev" "$source_url" sha256:base-g ""
    labels running go2rtc "$(built_from go2rtc tree-g)"
    labels published go2rtc "$(built_from go2rtc tree-g)"
    labels running go2rtc "{\"$ns.go.modules\": \"golang.org/x/crypto@v0.57.0 golang.org/x/net@v0.59.0\"}"
    labels published go2rtc "{\"$ns.go.modules\": \"golang.org/x/crypto@v0.58.0 golang.org/x/net@v0.59.0\"}"
    check
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .reasons | join(";")')" \
        "compiled with newer Go modules: golang.org/x/crypto@v0.58.0" "the module that moved, and only it"
fi

if test_case "the application's entry counts the commits that changed compose.yaml"; then
    newer_app
    export STUB_DIGEST_proxy="sha256:newer-proxy"
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    published proxy "$published_rev" "$source_url" sha256:base-n ""
    history "$published_rev c1 fix(A): one" "c1 $running_rev feat(Cameras): compose" "$running_rev old feat(B): two"
    path_history compose.yaml "c1 $running_rev feat(Cameras): compose"
    check
    assert_equals "$(app .compose.commits)" "1" "one commit changed it"
    assert_equals "$(app .compose.found)" "true" "counted to the running revision"
    assert_equals "$(field '.services[] | select(.service == "proxy") | has("compose")')" "false" \
        "and only the application's entry says so"
fi

if test_case "a current image says which revision it runs"; then
    check
    assert_equals "$(field '[.services[].running.revision] | unique | join(",")')" "$running_rev" \
        "so a reader can tell the containers apart"
fi

if test_case "an image built from source has nothing to compare with, and nothing is asked"; then
    export STUB_REF_homespool="homespool" STUB_REF_proxy="homespool-proxy:latest"
    export STUB_REF_go2rtc="homespool-go2rtc:latest"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(field '[.services[].status] | join(",")')" "local,local,local" "all three are local builds"
    assert_not_contains "$log" "imagetools" "no registry is asked"
    assert_not_contains "$log" "curl" "and nothing else either"
fi

if test_case "an image pinned to a digest has no tag to follow"; then
    export STUB_REF_proxy="registry.example.net/homespool-proxy@sha256:abc"
    check
    assert_equals "$(field '.services[] | select(.service == "proxy") | .status')" "pinned" "pinned is reported"
fi

if test_case "a camera sidecar still on upstream's digest pin is reported as pinned"; then
    # What a deployment whose compose.yaml predates Homespool's own go2rtc image runs.
    export STUB_REF_go2rtc="alexxit/go2rtc@sha256:675c318b23c06fd862a61d262240c9a63436b4050d177ffc68a32710d9e05bae"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .status')" "pinned" "pinned is reported"
fi

if test_case "a newer camera sidecar image is compared like the other two"; then
    export STUB_DIGEST_go2rtc="sha256:newer-go2rtc"
    published go2rtc "$running_rev" "$source_url" sha256:base-h ""
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(field .update_available)" "true" "the sidecar alone makes an update available"
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .status')" "newer" "newer is reported"
    assert_contains "$(field '.services[] | select(.service == "go2rtc") | .reasons | join(";")')" \
        "rebuilt from the same revision" "a rebuild is named"
    assert_equals "$(field '.services[] | select(.service == "go2rtc") | .runtime')" "null" \
        "no .NET runtime is looked for in an image that has none"
fi

if test_case "a container that is not running is reported, and a project name that matches nothing too"; then
    export STUB_REF_proxy=""
    check
    assert_equals "$(field '.services[] | select(.service == "proxy") | .status')" "not-running" "missing proxy"
    export HOMESPOOL_PROJECT="other"
    check
    assert_equals "$(field '[.services[].status] | join(",")')" "not-running,not-running,not-running" \
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

if test_case "the report is copied into the application's volume, whole and readable by it"; then
    mkdir -p "$scratch/volume"
    export STUB_REPORT_VOLUME="$scratch/volume"
    newer_app
    published homespool "$running_rev" "$source_url" sha256:base-b 10.0.12
    check
    assert_status "$status" 0 "checks cleanly"
    if cmp -s "$scratch/state/update-check.json" "$scratch/volume/update-check.json"; then
        passed=$((passed + 1))
    else
        fail "the application's copy is the same report" "state: $report" \
            "volume: $(cat "$scratch/volume/update-check.json" 2>/dev/null)"
    fi
    assert_equals "$(ls -l "$scratch/volume/update-check.json" | cut -c1-10)" "-rw-r--r--" \
        "world-readable, because the application does not run as root"
    assert_equals "$(ls -A "$scratch/volume" | tr '\n' ' ')" "update-check.json " "and nothing is left beside it"
    assert_contains "$log" "inspect --format {{range .Mounts}}" "found through the container's own mounts"
fi

if test_case "a compose.yaml from before the volume is no fault: the host's copy is still written"; then
    export STUB_REPORT_VOLUME=""
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(field .schema)" "1" "the host's report is written"
fi

if test_case "no application container, no copy for it"; then
    mkdir -p "$scratch/volume"
    export STUB_REPORT_VOLUME="$scratch/volume" STUB_REF_homespool=""
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(ls -A "$scratch/volume" | tr '\n' ' ')" "" "the volume is left alone"
    assert_not_contains "$log" "Mounts" "and not even looked for"
fi

# One step alone, for the cases about what that step refuses.
step() {
    output="$("$posix_sh" "$script" "$1" 2> "$scratch/stderr")"
    status=$?
    log="$(cat "$STUB_LOG")"
}

if test_case "compare never touches the Docker daemon, only the registry and the web"; then
    newer_app
    published homespool "$published_rev" "$source_url" sha256:base-a 10.0.12
    history "$published_rev $running_rev fix(A): one" "$running_rev old feat(B): two"
    check
    assert_status "$status" 0 "checks cleanly"
    compare_calls="$(sed -n '/^== compare$/,/^== publish$/p' "$STUB_LOG" | grep -v '^==' |
        grep -v '^buildx imagetools inspect ' | grep -v '^curl ' || true)"
    assert_equals "$compare_calls" "" "nothing in compare but imagetools and curl"
    collect_calls="$(sed -n '/^== collect$/,/^== compare$/p' "$STUB_LOG" | grep -c -E '^(curl|buildx) ' || true)"
    assert_equals "$collect_calls" "0" "and nothing in collect reaches the network"
fi

if test_case "compare without collect refuses rather than report nothing running"; then
    step compare
    assert_status "$status" 1 "compare alone fails"
    assert_contains "$(cat "$scratch/stderr")" "collect runs first" "and says why"
fi

if test_case "an image built from source is reported with the date it was built"; then
    export STUB_REF_homespool="homespool"
    check
    assert_equals "$(app .status)" "local" "local"
    assert_equals "$(app .built)" "2026-08-14T02:34:59.915970875Z" "with the image's own build date"
fi

if test_case "publish checks the report as the state directory's owner, never as root"; then
    mkdir -p "$scratch/volume"
    export STUB_REPORT_VOLUME="$scratch/volume"
    check
    assert_status "$status" 0 "checks cleanly"
    assert_contains "$log" "setpriv --reuid=$(stat -c %u "$scratch/state") --regid=$(stat -c %g "$scratch/state") --clear-groups --no-new-privs jq -e" \
        "jq runs as the owner, with no groups and no way back"
fi

# The refusals: a full run first, so the volume holds a good report, then the state directory's copy
# is replaced with something else and publish is run alone. The good report must survive each.
refused() {
    mkdir -p "$scratch/volume"
    export STUB_REPORT_VOLUME="$scratch/volume"
    check
    cp "$scratch/volume/update-check.json" "$scratch/good.json"
    rm -f "$scratch/state/update-check.json"
}

assert_volume_untouched() {
    if cmp -s "$scratch/volume/update-check.json" "$scratch/good.json"; then
        passed=$((passed + 1))
    else
        fail "$1" "the volume's report changed: $(head -c 120 "$scratch/volume/update-check.json")"
    fi
}

if test_case "publish refuses a symlink in the report's place"; then
    refused
    printf 'SECRET\n' > "$scratch/secret"
    ln -s "$scratch/secret" "$scratch/state/update-check.json"
    step publish
    assert_status "$status" 1 "a symlink is refused"
    assert_contains "$(cat "$scratch/stderr")" "not a plain file" "and says why"
    assert_volume_untouched "and what it points at never reaches the application"
    assert_not_contains "$(cat "$scratch/volume/update-check.json")" "SECRET" "not a byte of it"
fi

if test_case "publish refuses a report at or over the limit"; then
    refused
    head -c 1048576 /dev/zero | tr '\0' ' ' > "$scratch/state/update-check.json"
    step publish
    assert_status "$status" 1 "an oversized report is refused"
    assert_contains "$(cat "$scratch/stderr")" "bytes or more" "and says why"
    assert_volume_untouched "the previous report stands"
fi

if test_case "publish refuses an empty report, one that is not JSON, and JSON that is not a report"; then
    refused
    : > "$scratch/state/update-check.json"
    step publish
    assert_status "$status" 1 "empty is refused"
    assert_contains "$(cat "$scratch/stderr")" "is empty" "as empty"
    printf 'not json at all\n' > "$scratch/state/update-check.json"
    step publish
    assert_status "$status" 1 "not JSON is refused"
    printf '{"schema":2,"services":[]}\n' > "$scratch/state/update-check.json"
    step publish
    assert_status "$status" 1 "another schema is refused"
    printf '{"schema":1,"services":"nope"}\n' > "$scratch/state/update-check.json"
    step publish
    assert_status "$status" 1 "services that are not a list are refused"
    assert_contains "$(cat "$scratch/stderr")" "is not a report" "as not a report"
    assert_volume_untouched "the previous report stands through all of them"
fi

if test_case "collect records which image each container runs, where only root can write"; then
    export STUB_REF_go2rtc=""
    check
    assert_status "$status" 0 "checks cleanly"
    assert_equals "$(cat "$scratch/watch/images")" "homespool image-homespool
proxy image-proxy
go2rtc -" "a line per service, - for one not running"
    assert_equals "$(ls -ld "$scratch/watch" | cut -c1-10)" "drwx------" "in a directory only its owner can read"
    assert_equals "$(ls -A "$scratch/watch" | tr '\n' ' ')" "images " "replaced whole, nothing left beside it"
    assert_equals "$(ls -A "$scratch/state" | tr '\n' ' ')" "update-check.json " \
        "and not in the state directory, which the unprivileged step owns"
fi

if test_case "watch starts no check while the images are the ones last checked"; then
    check
    : > "$STUB_LOG"
    step watch
    assert_status "$status" 0 "watch succeeds"
    assert_not_contains "$log" "systemctl" "no check is started"
    assert_equals "$output" "" "and nothing is logged"
fi

if test_case "watch starts a check when a container runs another image, and asks nothing outside"; then
    check
    : > "$STUB_LOG"
    export STUB_IMAGE_proxy="sha256:pulled-proxy"
    step watch
    assert_status "$status" 0 "watch succeeds"
    assert_contains "$log" "systemctl start --no-block homespool-update-check.service" "the check is started"
    assert_contains "$output" "runs again" "and the journal says why"
    assert_equals "$(grep -c -E '^(curl|buildx) ' "$STUB_LOG")" "0" "nothing reaches the network"
fi

if test_case "watch starts a check when a container stopped or started"; then
    check
    : > "$STUB_LOG"
    export STUB_REF_proxy=""
    step watch
    assert_contains "$log" "systemctl start" "a stopped container is a change"
    check
    export STUB_REF_proxy="registry.example.net/homespool-proxy"
    : > "$STUB_LOG"
    step watch
    assert_contains "$log" "systemctl start" "and so is one started again"
fi

if test_case "watch starts a check when no check ever ran"; then
    step watch
    assert_status "$status" 0 "watch succeeds"
    assert_contains "$log" "systemctl start --no-block homespool-update-check.service" "the first check is started"
fi

if test_case "a check that failed after collect is not started again by watch"; then
    export STUB_REGISTRY_FAILS=1
    check
    assert_status "$status" 1 "the check fails"
    : > "$STUB_LOG"
    step watch
    assert_not_contains "$log" "systemctl" "the daily run retries it, not every watch"
fi

if test_case "watch follows the compose project, as collect does"; then
    export HOMESPOOL_PROJECT="other"
    check
    : > "$STUB_LOG"
    step watch
    assert_not_contains "$log" "systemctl" "the same project's containers, unchanged"
    assert_contains "$log" "project=other" "asked of that project"
fi

if test_case "without a subcommand it says how to run it and does nothing"; then
    step ""
    assert_status "$status" 2 "usage"
    assert_contains "$(cat "$scratch/stderr")" "start homespool-update-check.service" "and points at the service"
    assert_equals "$log" "" "nothing is asked of Docker"
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
