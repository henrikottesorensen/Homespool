#!/usr/bin/env bash
#
# Tests for tools/go-module-versions.sh - that it resolves each module the Dockerfile names to the
# release the module proxy calls latest, and that it prints nothing rather than part of the list.
#
#   tests/go-module-versions.test.sh             # run them all
#   tests/go-module-versions.test.sh refuses     # run only tests whose name contains "refuses"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# curl is a stub answering from fixtures, in the shape proxy.golang.org answers @latest; nothing
# reaches the network.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/tools/go-module-versions.sh"
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

assert_equals() {
    if [ "$1" = "$2" ]; then passed=$((passed + 1)); else fail "$3" "expected: $2" "actual:   $1"; fi
}

assert_contains() {
    case "$1" in
        *"$2"*) passed=$((passed + 1)) ;;
        *) fail "$3" "expected to contain: $2" "actual: $1" ;;
    esac
}

assert_status() {
    if [ "$1" = "$2" ]; then passed=$((passed + 1)); else fail "$3" "expected exit $2, got $1"; fi
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/go-module-versions.XXXXXX")" && pwd)"
    mkdir -p "$scratch/bin" "$scratch/fixtures"

    # Every URL asked for, and the proxy's answer for a module that has a fixture; 404 otherwise.
    cat > "$scratch/bin/curl" <<'STUB'
#!/bin/sh
url=""; for a in "$@"; do url="$a"; done
printf '%s\n' "$url" >> "$STUB_LOG"
module="${url#https://proxy.example.net/}"; module="${module%/@latest}"
fixture="$STUB_FIXTURES/$(printf '%s' "$module" | tr '/' '_')"
[ -f "$fixture" ] || exit 22
cat "$fixture"
STUB
    chmod 755 "$scratch/bin/curl"

    printf '{"Version":"v0.57.0","Time":"2026-09-08T18:05:01Z","Origin":{"VCS":"git","Ref":"refs/tags/v0.57.0"}}\n' \
        > "$scratch/fixtures/golang.org_x_crypto"
    printf '{"Version":"v0.59.0","Time":"2026-09-08T19:18:02Z","Origin":{"VCS":"git","Ref":"refs/tags/v0.59.0"}}\n' \
        > "$scratch/fixtures/golang.org_x_net"
    printf 'FROM scratch\nARG GO2RTC_UPDATED_MODULES="golang.org/x/crypto golang.org/x/net"\n' > "$scratch/Dockerfile"

    export PATH="$scratch/bin:$PATH" STUB_LOG="$scratch/log" STUB_FIXTURES="$scratch/fixtures"
    export GOPROXY_URL="https://proxy.example.net"
    : > "$STUB_LOG"
}

resolve() {
    output="$("$script" "$scratch/Dockerfile" 2> "$scratch/stderr")"
    status=$?
    stderr="$(cat "$scratch/stderr")"
}

if test_case "each module named in the Dockerfile is resolved to the release the proxy calls latest"; then
    resolve
    assert_status "$status" 0 "both resolve"
    assert_equals "$output" "golang.org/x/crypto@v0.57.0 golang.org/x/net@v0.59.0" "as go get takes them, in the Dockerfile's order"
    assert_equals "$(cat "$STUB_LOG")" \
        "$(printf 'https://proxy.example.net/golang.org/x/crypto/@latest\nhttps://proxy.example.net/golang.org/x/net/@latest')" \
        "one @latest question per module, of the proxy it was given"
fi

if test_case "a module the proxy cannot answer for fails the run and prints nothing"; then
    rm "$scratch/fixtures/golang.org_x_net"
    resolve
    assert_status "$status" 1 "part of the list is not a list"
    assert_equals "$output" "" "nothing reaches the build"
    assert_contains "$stderr" "could not ask https://proxy.example.net for golang.org/x/net" "and it says which"
fi

if test_case "an answer with no version fails the run"; then
    printf '{"Error":"not found"}\n' > "$scratch/fixtures/golang.org_x_crypto"
    resolve
    assert_status "$status" 1 "no version, no list"
    assert_equals "$output" "" "nothing reaches the build"
fi

if test_case "refuses a Dockerfile without the module list"; then
    printf 'FROM scratch\n' > "$scratch/Dockerfile"
    resolve
    assert_status "$status" 1 "nothing to resolve is a fault, not an empty answer"
    assert_contains "$stderr" "no 'ARG GO2RTC_UPDATED_MODULES=" "and it says what is missing"
fi

if test_case "refuses a module path with capital letters rather than ask for it unescaped"; then
    printf 'ARG GO2RTC_UPDATED_MODULES="github.com/BurntSushi/toml"\n' > "$scratch/Dockerfile"
    resolve
    assert_status "$status" 1 "refused"
    assert_equals "$(cat "$STUB_LOG")" "" "and the proxy is never asked"
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
