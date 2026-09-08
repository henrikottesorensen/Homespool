#!/usr/bin/env bash
#
# Tests for nginx/docker-entrypoint.d/26-user-tls-servers.sh - the per-name TLS server blocks, and
# which of them carry Strict-Transport-Security.
#
#   tests/user-tls-servers.test.sh              # run them all
#   tests/user-tls-servers.test.sh hsts         # run only tests whose name contains "hsts"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# The script is RUN, not sourced, exactly as the base image's entrypoint runs it, under dash where
# there is one for the reason tests/user-server-names.test.sh gives. Its three paths are overridden
# through the HOMESPOOL_* variables it reads for this purpose, so every case works in a scratch
# directory and nothing here touches /etc/nginx.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/nginx/docker-entrypoint.d/26-user-tls-servers.sh"
filter="${1:-}"

if command -v dash >/dev/null 2>&1; then
    posix_sh="$(command -v dash)"
elif [ "${BASH_VERSINFO[0]:-0}" -ge 4 ]; then
    posix_sh="$BASH"
else
    echo "SKIP  tests/user-tls-servers.test.sh: needs dash or bash 4+ to run the script under test."
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

assert_not_contains() {
    case "$1" in
        *"$2"*) fail "$3" "expected NOT to contain: $2" "actual: $1" ;;
        *) passed=$((passed + 1)) ;;
    esac
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(mktemp -d "${TMPDIR:-/tmp}/user-tls.XXXXXX")"
    mkdir -p "$scratch/certs/certificates" "$scratch/conf.d"
    printf '# body\n' > "$scratch/body.conf"
    return 0
}

# A certificate pair where 25 would have minted one (self-signed), or where an ACME client writes
# (issued). Contents do not matter: the script tests for a non-empty file, never reads it.
self_signed() { printf 'x' > "$scratch/certs/$1.crt"; printf 'x' > "$scratch/certs/$1.key"; }
issued()      { printf 'x' > "$scratch/certs/certificates/$1.crt"; printf 'x' > "$scratch/certs/certificates/$1.key"; }

# Runs the generator with the given USER_TLS_NAMES and HSTS, capturing stderr for assertions.
generate() {
    HOMESPOOL_CERT_DIR="$scratch/certs" \
    HOMESPOOL_CONF_DIR="$scratch/conf.d" \
    HOMESPOOL_TLS_BODY="$scratch/body.conf" \
    USER_TLS_NAMES="$1" HSTS="${2:-}" \
        "$posix_sh" "$script" 2>&1
}

block() { cat "$scratch/conf.d/homespool-user-$1.conf" 2>/dev/null; }

if test_case "a self-signed name gets a block and no HSTS by default"; then
    self_signed homespool.lan
    out="$(generate "homespool.lan")"
    assert_contains "$(block homespool.lan)" "server_name homespool.lan;" "the block is written"
    assert_contains "$(block homespool.lan)" "certs/homespool.lan.crt" "with the self-signed pair"
    assert_not_contains "$(block homespool.lan)" "Strict-Transport-Security" "and no HSTS"
    assert_not_contains "$out" "HSTS" "nothing said about HSTS when it is not set"
fi

if test_case "hsts is sent on a name with an issued certificate"; then
    issued homespool.example.com
    out="$(generate "homespool.example.com" 1)"
    assert_contains "$(block homespool.example.com)" "certificates/homespool.example.com.crt" "the issued pair outranks a self-signed one"
    assert_contains "$(block homespool.example.com)" 'add_header Strict-Transport-Security "max-age=63072000" always;' "HSTS in the block"
    assert_contains "$out" "HSTS on" "and said so"
fi

if test_case "hsts is never sent on a self-signed name, and the refusal is said out loud"; then
    # The lock-out this exists to prevent: a browser told to refuse certificate warnings for a name
    # whose certificate nobody vouches for cannot reach that name again until max-age runs down.
    self_signed homespool.lan
    out="$(generate "homespool.lan" 1)"
    assert_not_contains "$(block homespool.lan)" "Strict-Transport-Security" "no HSTS on a self-signed name"
    assert_contains "$out" "HSTS is set but homespool.lan has a self-signed certificate" "and the operator is told why"
fi

if test_case "hsts on a mixed deployment lands on the issued name alone"; then
    issued homespool.example.com
    self_signed homespool.lan
    out="$(generate "homespool.example.com;homespool.lan" 1)"
    assert_contains "$(block homespool.example.com)" "Strict-Transport-Security" "the public name carries it"
    assert_not_contains "$(block homespool.lan)" "Strict-Transport-Security" "the LAN name does not"
fi

if test_case "hsts is off when the setting is empty, even on an issued name"; then
    issued homespool.example.com
    generate "homespool.example.com" "" >/dev/null
    assert_not_contains "$(block homespool.example.com)" "Strict-Transport-Security" "empty means off"
fi

if test_case "a name with no certificate at all is skipped, whatever HSTS says"; then
    out="$(generate "nowhere.lan" 1)"
    [ -f "$scratch/conf.d/homespool-user-nowhere.lan.conf" ] && fail "a block was written for a name with no certificate" || passed=$((passed + 1))
    assert_contains "$out" "no certificate for nowhere.lan" "and said so"
fi

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed assertions passed."
else
    echo "$failed failed, $passed passed."
    exit 1
fi
