#!/usr/bin/env bash
#
# Tests for acme/homespool-check-cert.sh - chiefly that a failure to look is never reported as
# there being nothing to look at.
#
#   tests/acme-check-cert.test.sh               # run them all
#   tests/acme-check-cert.test.sh failed        # run only tests whose name contains "failed"
#
# Same harness as tests/acme-renew-cert.test.sh: the script is RUN under dash where there is one,
# `docker` is a stub on PATH, and COMPOSE_DIR is a scratch directory. The stub answers the two
# reads the script makes from the environment and never evaluates the `sh -c` payload.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/acme/homespool-check-cert.sh"
filter="${1:-}"

if command -v dash >/dev/null 2>&1; then
    posix_sh="$(command -v dash)"
elif [ "${BASH_VERSINFO[0]:-0}" -ge 4 ]; then
    posix_sh="$BASH"
else
    echo "SKIP  tests/acme-check-cert.test.sh: needs dash or bash 4+ to run the script under test."
    exit 0
fi

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

assert_status() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "$3" "expected exit $2, got $1" "output: $out"
    fi
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(mktemp -d "${TMPDIR:-/tmp}/acme-check.XXXXXX")"
    mkdir -p "$scratch/bin" "$scratch/compose"
    printf '# compose\n' > "$scratch/compose/compose.yaml"

    cat > "$scratch/bin/docker" <<'STUB'
#!/bin/sh
payload=''
for a in "$@"; do payload="$a"; done

case "$payload" in
    'printf %s "${ACME_HOSTS:-}"')
        if [ -n "${STUB_HOSTS_FAIL:-}" ]; then
            echo "permission denied reading /etc/lego/dns.env" >&2
            exit 1
        fi
        printf %s "$STUB_ACME_HOSTS"
        ;;
    *'openssl x509'*)
        if [ -n "${STUB_REPORT_FAIL:-}" ]; then
            echo "Cannot connect to the Docker daemon" >&2
            exit 1
        fi
        printf '%s\n' "${STUB_REPORT:-}"
        ;;
esac
exit 0
STUB
    chmod +x "$scratch/bin/docker"
    return 0
}

# check HOSTS [REPORT] [HOSTS_FAIL] [REPORT_FAIL]
check() {
    out="$(
        PATH="$scratch/bin:$PATH" \
        COMPOSE_DIR="$scratch/compose" \
        STUB_ACME_HOSTS="$1" \
        STUB_REPORT="${2:-}" \
        STUB_HOSTS_FAIL="${3:-}" \
        STUB_REPORT_FAIL="${4:-}" \
            "$posix_sh" "$script" 2>&1
    )"
    status=$?
}

far="$(date -d '+200 days' '+%b %e %H:%M:%S %Y GMT')"
soon="$(date -d '+5 days' '+%b %e %H:%M:%S %Y GMT')"

if test_case "an empty ACME_HOSTS is quiet and succeeds"; then
    check ""
    assert_status "$status" 0 "nothing to expire is not a fault"
    [ -z "$out" ] && passed=$((passed + 1)) || fail "and says nothing" "output: $out"
fi

if test_case "a failed read of ACME_HOSTS is critical, not an empty one"; then
    check "homespool.example.com" "" 1
    assert_status "$status" 2 "an unreadable configuration is the expired-or-unreadable code"
    assert_contains "$out" "could not read ACME_HOSTS" "and says so"
    assert_contains "$out" "permission denied reading /etc/lego/dns.env" "with the reason compose gave"
fi

if test_case "a failed run of the proxy image is critical and shows why"; then
    check "homespool.example.com" "" "" 1
    assert_status "$status" 2 "a daemon error is critical"
    assert_contains "$out" "could not run the proxy image" "and says so"
    assert_contains "$out" "Cannot connect to the Docker daemon" "with the daemon's own message"
fi

if test_case "a certificate far from expiry is quiet"; then
    check "homespool.example.com" "homespool.example.com $far"
    assert_status "$status" 0 "fine"
fi

if test_case "a certificate inside the warning window warns"; then
    check "homespool.example.com" "homespool.example.com $soon"
    assert_status "$status" 1 "inside the window"
    assert_contains "$out" "expires in" "and says how long"
fi

if test_case "a missing certificate is critical"; then
    check "homespool.example.com" "homespool.example.com MISSING"
    assert_status "$status" 2 "missing"
    assert_contains "$out" "no readable certificate" "and says so"
fi

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed assertions passed."
else
    echo "$failed failed, $passed passed."
    exit 1
fi
