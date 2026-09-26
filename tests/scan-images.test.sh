#!/usr/bin/env bash
#
# Tests for tools/scan-images.sh - what it counts as relevant, what it records without counting, and
# that it pushes nothing when it could not see everything.
#
#   tests/scan-images.test.sh               # run them all
#   tests/scan-images.test.sh runtime       # run only tests whose name contains "runtime"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# docker, curl and oras are stubs on PATH; jq is the real one, because the verdict is jq and that is
# what is under test. The docker stub answers the registry reads from fixed names, and plays Trivy by
# copying a fixture report to the path the script asked for. curl answers the .NET release metadata
# and GitHub from fixture files a case writes. Nothing reaches a daemon, a registry or the network.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/tools/scan-images.sh"
filter="${1:-}"

if ! command -v jq >/dev/null 2>&1; then
    echo "SKIP  tests/scan-images.test.sh: needs jq, which the script under test needs too."
    exit 0
fi

passed=0
failed=0
scratch=""
revision="0123456789abcdef0123456789abcdef01234567"

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

# A Trivy report for one image: no vulnerabilities, unless given one as "id severity package".
trivy_report() {
    if [ $# -eq 0 ]; then
        printf '{"Results":[{"Class":"os-pkgs","Type":"debian"}]}\n'
    else
        printf '{"Results":[{"Class":"os-pkgs","Vulnerabilities":[{"VulnerabilityID":"%s","Severity":"%s","PkgName":"%s","InstalledVersion":"1.0","FixedVersion":"1.1"}]}]}\n' "$1" "$2" "$3"
    fi
}

# The channel's releases, newest first. The installed runtime is 10.0.12; a case prepends newer ones.
releases() {
    printf '{"latest-runtime":"%s","releases":[%s{"release-version":"10.0.12","release-date":"2026-09-08","security":true,"cve-list":[{"cve-id":"CVE-2026-69439"}]},{"release-version":"10.0.11","release-date":"2026-08-11","security":true,"cve-list":[]}]}\n' \
        "${1:-10.0.12}" "${2:-}"
}

# A compare answer from GitHub, one commit per subject; "merge:<subject>" makes it a merge.
compare() {
    local commits="" subject parents
    for subject in "$@"; do
        parents='[{"sha":"a"}]'
        case "$subject" in merge:*) parents='[{"sha":"a"},{"sha":"b"}]'; subject="${subject#merge:}" ;; esac
        commits="$commits${commits:+,}{\"parents\":$parents,\"commit\":{\"message\":\"$subject\\n\\nbody\"}}"
    done
    printf '{"status":"ahead","ahead_by":%s,"commits":[%s]}\n' "$#" "$commits"
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/scan-images.XXXXXX")" && pwd)"
    mkdir -p "$scratch/bin" "$scratch/fixtures"

    cat > "$scratch/bin/docker" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$STUB_LOG"
case "$1" in
    compose)
        [ -n "${STUB_REGISTRY:-}" ] && prefix="$STUB_REGISTRY/" || prefix=""
        printf '%shomespool:latest\n%shomespool-proxy:latest\n' "$prefix" "$prefix"
        ;;
    buildx)
        # buildx imagetools inspect --format <format> <reference>
        case "$6" in
            mcr.microsoft.com/*) echo "sha256:${STUB_ASPNET_NOW:-aspnet-built}" ;;
            nginxinc/*) echo "sha256:nginx-built" ;;
            *homespool-proxy:*) echo "sha256:image-proxy" ;;
            *) echo "sha256:image-app" ;;
        esac
        ;;
    image)
        # image inspect --format <format> <reference>
        case "$5" in *homespool-proxy@*) name=proxy ;; *) name=app ;; esac
        case "$4" in
            *revision*) echo "${STUB_REVISION:-0123456789abcdef0123456789abcdef01234567}" ;;
            *source*) echo "https://github.com/example/homespool" ;;
            *base.name*) [ $name = app ] && echo "mcr.microsoft.com/dotnet/aspnet:10.0" || echo "nginxinc/nginx-unprivileged:stable" ;;
            *base.digest*) [ $name = app ] && echo "sha256:aspnet-built" || echo "sha256:nginx-built" ;;
            *Env*) [ $name = app ] && printf 'PATH=/usr/bin\nASPNET_VERSION=10.0.12\n' || printf 'PATH=/usr/bin\n' ;;
        esac
        ;;
    save)
        # save <reference> -o <path>
        : > "$4"
        ;;
    run)
        # run --rm -v <work>:/work -v <cache>:/root/.cache <image> image ... --input /work/<n>.tar --output /work/<f>
        work=""; input=""; output=""; previous=""
        for a in "$@"; do
            case "$previous" in
                -v) case "$a" in *:/work) work="${a%:/work}" ;; esac ;;
                --input) input="${a#/work/}" ;;
                --output) output="${a#/work/}" ;;
            esac
            previous="$a"
        done
        cp "$STUB_FIXTURES/trivy-${input%.tar}.json" "$work/$output"
        ;;
esac
exit 0
STUB

    cat > "$scratch/bin/curl" <<'STUB'
#!/bin/sh
url=""; for a in "$@"; do url="$a"; done
printf 'curl %s\n' "$url" >> "$STUB_LOG"
case "$url" in
    *"${STUB_CURL_FAILS:-no-such-url}"*) exit 22 ;;
esac
case "$url" in
    *releases-index.json) printf '{"releases-index":[{"channel-version":"10.0","releases.json":"https://example.invalid/10.0/releases.json"}]}\n' ;;
    */10.0/releases.json) cat "$STUB_FIXTURES/releases.json" ;;
    */compare/*) cat "$STUB_FIXTURES/compare.json" ;;
    https://api.github.com/repos/*) printf '{"default_branch":"main"}\n' ;;
    *) exit 22 ;;
esac
STUB

    cat > "$scratch/bin/oras" <<'STUB'
#!/bin/sh
printf 'oras %s\n' "$*" >> "$STUB_LOG"
STUB
    chmod 755 "$scratch/bin/docker" "$scratch/bin/curl" "$scratch/bin/oras"

    trivy_report > "$scratch/fixtures/trivy-homespool.json"
    trivy_report > "$scratch/fixtures/trivy-homespool-proxy.json"
    releases > "$scratch/fixtures/releases.json"
    compare > "$scratch/fixtures/compare.json"

    export PATH="$scratch/bin:$PATH"
    export STUB_LOG="$scratch/log"
    export STUB_FIXTURES="$scratch/fixtures"
    export STUB_REGISTRY="registry.example.net"
    export HOMESPOOL_TRIVY_CACHE="$scratch/cache"
    unset STUB_REVISION STUB_ASPNET_NOW STUB_CURL_FAILS GITHUB_TOKEN
    : > "$STUB_LOG"
    return 0
}

scan() {
    verdict="$("$script" "$@" 2> "$scratch/stderr")"
    status=$?
    log="$(cat "$STUB_LOG")"
}

field() { printf '%s' "$verdict" | jq -r "$1"; }

if test_case "nothing to take: not relevant, and the verdict is pushed"; then
    scan
    assert_status "$status" 0 "a clean scan succeeds"
    assert_equals "$(field .relevant)" "false" "nothing to take is not relevant"
    assert_equals "$(field '.images | length')" "2" "both images are judged"
    assert_equals "$(field .schema)" "1" "the verdict says which schema it is"
    assert_contains "$(field .scanner)" "aquasec/trivy:0.74.0@sha256:" "and which scanner, pinned"
    assert_contains "$log" "oras push --artifact-type application/vnd.homespool.verdict.v1+json" \
        "the verdict is pushed as its own artifact type"
    assert_contains "$log" "registry.example.net/homespool-verdict:latest verdict.json:" \
        "beside the images, under latest"
    assert_contains "$log" "save registry.example.net/homespool@sha256:image-app -o" \
        "the scanner is handed a saved image, by digest"
fi

if test_case "a fixable high vulnerability is relevant"; then
    trivy_report CVE-2026-0001 HIGH openssl > "$scratch/fixtures/trivy-homespool-proxy.json"
    scan
    assert_equals "$(field .relevant)" "true" "a fixable HIGH is relevant"
    assert_contains "$(field '.reasons[]')" "homespool-proxy: 1 critical or high" "and says where"
    assert_equals "$(field '.images[] | select(.name == "homespool-proxy") | .vulnerabilities[0].fixed')" \
        "1.1" "the fixed version is recorded"
fi

if test_case "a newer runtime that is a security release is relevant"; then
    releases 10.0.13 '{"release-version":"10.0.13","release-date":"2026-10-13","security":true,"cve-list":[{"cve-id":"CVE-2026-9999"}]},' \
        > "$scratch/fixtures/releases.json"
    scan
    assert_equals "$(field .relevant)" "true" "a newer security runtime is relevant"
    assert_contains "$(field '.reasons[]')" ".NET 10.0.13 is a security release" "and names it"
    assert_equals "$(field '.images[] | select(.name == "homespool") | .runtime.newer[0].cves[0]')" \
        "CVE-2026-9999" "with its CVEs"
    assert_equals "$(field '.images[] | select(.name == "homespool") | .runtime.installed')" \
        "10.0.12" "the installed runtime is read from the image"
fi

if test_case "a newer runtime that fixes no security issue is recorded but not relevant"; then
    releases 10.0.13 '{"release-version":"10.0.13","release-date":"2026-10-13","security":false,"cve-list":[]},' \
        > "$scratch/fixtures/releases.json"
    scan
    assert_equals "$(field .relevant)" "false" "a plain bug-fix runtime is not relevant"
    assert_equals "$(field '.images[] | select(.name == "homespool") | .runtime.newer | length')" \
        "1" "but it is in the verdict"
fi

if test_case "a runtime preview in the channel is never counted as newer"; then
    releases 10.0.12 '{"release-version":"10.0.100-rc.1","release-date":"2026-10-01","security":true,"cve-list":[]},' \
        > "$scratch/fixtures/releases.json"
    scan
    assert_equals "$(field .relevant)" "false" "a preview is not a release to take"
fi

if test_case "Homespool fixes since the revision are relevant, counted by type"; then
    compare "fix(Printers): one" "fix(Host): two" "feat(Files): three" "merge:Merge branch 'x'" "Tidy things" \
        > "$scratch/fixtures/compare.json"
    scan
    assert_equals "$(field .relevant)" "true" "fixes since the revision are relevant"
    assert_contains "$(field '.reasons[]')" "Homespool has 2 fixes since 0123456789ab" "and counts them"
    assert_equals "$(field '.images[0].homespool.by_type | tojson')" '{"feat":1,"fix":2,"other":1}' \
        "subjects are counted by type, merges left out"
    assert_equals "$(field '[.reasons[] | select(startswith("Homespool"))] | length')" "1" \
        "one reason for the shared revision, not one per image"
fi

if test_case "features alone are not relevant"; then
    compare "feat(Files): three" > "$scratch/fixtures/compare.json"
    scan
    assert_equals "$(field .relevant)" "false" "a new feature is not a fix"
fi

if test_case "a republished base is recorded but is not by itself relevant"; then
    export STUB_ASPNET_NOW="aspnet-republished"
    scan
    assert_equals "$(field '.images[] | select(.name == "homespool") | .base.republished')" "true" \
        "the republish is recorded"
    assert_equals "$(field .relevant)" "false" "and is not a reason on its own"
fi

if test_case "--no-push scans and prints but pushes nothing"; then
    scan --no-push
    assert_status "$status" 0 "--no-push succeeds"
    assert_equals "$(field .schema)" "1" "the verdict is still printed"
    assert_not_contains "$log" "oras" "nothing is pushed"
fi

if test_case "refuses without a registry, before touching an image"; then
    STUB_REGISTRY=""
    scan
    assert_status "$status" 1 "no registry is refused"
    assert_contains "$(cat "$scratch/stderr")" "REGISTRY is not set" "and says why"
    assert_not_contains "$log" "pull" "nothing is pulled"
fi

if test_case "a failed source fails the run and pushes nothing"; then
    export STUB_CURL_FAILS="releases.json"
    scan
    assert_status "$status" 1 "an unreachable release feed fails the scan"
    assert_contains "$(cat "$scratch/stderr")" "could not fetch https://example.invalid/10.0/releases.json" \
        "and names the source it could not reach"
    assert_not_contains "$log" "oras" "and the previous verdict is left standing"
fi

if test_case "an image whose revision is not a clean commit is refused, and nothing is pushed"; then
    export STUB_REVISION="0123456789abcdef0123456789abcdef01234567.dirty"
    scan
    assert_status "$status" 1 "a modified revision is refused"
    assert_contains "$(cat "$scratch/stderr")" "not a clean commit" "and says why"
    assert_not_contains "$log" "oras" "nothing is pushed"
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
