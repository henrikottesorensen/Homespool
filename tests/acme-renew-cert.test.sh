#!/usr/bin/env bash
#
# Tests for acme/homespool-renew-cert.sh - which names it will act on, and which it refuses before
# they reach a shell.
#
#   tests/acme-renew-cert.test.sh               # run them all
#   tests/acme-renew-cert.test.sh hostile       # run only tests whose name contains "hostile"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# The script is RUN, not sourced, under dash where there is one, exactly as the systemd unit runs
# it. It talks to the world only through `docker`, so a stub of that on PATH is the whole harness:
# every invocation is recorded, and the three reads the script makes are answered from the
# environment. COMPOSE_DIR points at a scratch directory, so nothing here touches a deployment.
#
# THE VERIFICATION PAYLOAD IS RECORDED AND NEVER EVALUATED. The string the script builds for
# `sh -c` is what most of these cases are about, and a stub that ran it would be running the
# injection these tests exist to prove is not constructed.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/acme/homespool-renew-cert.sh"
filter="${1:-}"

if command -v dash >/dev/null 2>&1; then
    posix_sh="$(command -v dash)"
elif [ "${BASH_VERSINFO[0]:-0}" -ge 4 ]; then
    posix_sh="$BASH"
else
    echo "SKIP  tests/acme-renew-cert.test.sh: needs dash or bash 4+ to run the script under test."
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

assert_status() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "$3" "expected exit $2, got $1"
    fi
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(mktemp -d "${TMPDIR:-/tmp}/acme-renew.XXXXXX")"
    mkdir -p "$scratch/bin" "$scratch/compose"

    # The deployment directory the script cds into. A compose file and something that looks like a
    # key sit beside it, so a case can prove a glob in ACME_HOSTS does not become their names.
    printf '# compose\n' > "$scratch/compose/compose.yaml"
    printf 'x\n' > "$scratch/compose/secret.pem"

    cat > "$scratch/bin/docker" <<'STUB'
#!/bin/sh
# Every invocation, flattened, for assertions about what was asked of the daemon.
printf '%s\n' "$*" >> "$STUB_LOG"

# The `-c` payload is the last argument when there is one.
payload=''
for a in "$@"; do payload="$a"; done

case "$payload" in
    'printf %s "${ACME_HOSTS:-}"')
        printf %s "$STUB_ACME_HOSTS"
        ;;
    'cd /certs/certificates'*)
        # The fingerprint, taken either side of the run. The same answer both times unless the case
        # asked for a change, which is what decides whether the proxy is restarted.
        calls=0
        [ -f "$STUB_DIR/fingerprints" ] && calls="$(cat "$STUB_DIR/fingerprints")"
        calls=$((calls + 1))
        printf %s "$calls" > "$STUB_DIR/fingerprints"
        if [ -n "${STUB_CERTS_CHANGE:-}" ] && [ "$calls" -gt 1 ]; then
            echo "after"
        else
            echo "before"
        fi
        ;;
    '[ -s /certs/certificates/'*)
        # Recorded, deliberately not run. Reported as present, so a case that is not about the
        # verification does not also trip the missing-certificate branch.
        printf '%s\n' "$payload" >> "$STUB_VERIFY_LOG"
        ;;
esac

exit 0
STUB
    chmod +x "$scratch/bin/docker"
    return 0
}

# Runs the renewal with the given ACME_HOSTS, capturing stdout and stderr together and the status.
renew() {
    out="$(
        PATH="$scratch/bin:$PATH" \
        COMPOSE_DIR="$scratch/compose" \
        STUB_DIR="$scratch" \
        STUB_LOG="$scratch/docker.log" \
        STUB_VERIFY_LOG="$scratch/verify.log" \
        STUB_ACME_HOSTS="$1" \
        STUB_CERTS_CHANGE="${2:-}" \
            "$posix_sh" "$script" 2>&1
    )"
    status=$?
}

asked()    { cat "$scratch/docker.log" 2>/dev/null; }
verified() { cat "$scratch/verify.log" 2>/dev/null; }

if test_case "an ordinary name is issued once, verified, and nothing is restarted when it has not changed"; then
    renew "homespool.example.com"
    assert_status "$status" 0 "a clean run succeeds"
    assert_contains "$(asked)" "LEGO_DOMAINS=homespool.example.com" "the name is asked for"
    assert_contains "$(verified)" "/certs/certificates/homespool.example.com.crt" "and verified afterwards"
    assert_not_contains "$(asked)" "restart proxy" "an unchanged certificate does not restart the proxy"
    assert_contains "$out" "no change" "and says so"
fi

if test_case "a certificate that actually changed restarts the proxy"; then
    renew "homespool.example.com" 1
    assert_status "$status" 0 "the run still succeeds"
    assert_contains "$(asked)" "restart proxy" "the proxy is restarted"
    assert_contains "$out" "certificates changed" "and says why"
fi

if test_case "a hostile name never reaches a shell, and is refused by name"; then
    # The shape that makes the verification payload three commands instead of one: close the test
    # the script opens, run something, and leave a fragment that parses.
    #
    # NO WHITESPACE IN IT, deliberately. The trim above strips every space, so a payload written
    # with them arrives as one word and does not run - which makes a spaced payload the one case
    # this would pass without a guard at all. A command taking no arguments needs no space.
    renew 'x];touch${IFS}INJECTED;['
    assert_contains "$out" "not a usable hostname" "the name is refused"
    assert_not_contains "$(verified)" "INJECTED" "no payload is built from it"
    assert_not_contains "$(verified)" "touch" "nothing shell-shaped reaches sh -c"
    assert_not_contains "$(asked)" "LEGO_DOMAINS=x]" "and no certificate is asked for"
    [ -e "$scratch/compose/INJECTED" ] && fail "the injected command ran" || passed=$((passed + 1))
fi

if test_case "a refused name fails the unit, so the timer reports it"; then
    # An underscore, which is the realistic version of this: legal in a DNS label, outside the
    # hostname character set, and refused by every authority.
    renew "no_good.example.com"
    assert_status "$status" 1 "a name that cannot be used is a failure"
    assert_contains "$out" "refusing no_good.example.com" "and named in the refusal"
fi

if test_case "a leading dot is refused"; then
    # A name that is really a suffix. It would become /certs/certificates/.example.com.crt, which is
    # not what anybody meant by it.
    renew ".example.com"
    assert_contains "$out" "refusing .example.com" "refused"
    assert_not_contains "$(asked)" "LEGO_DOMAINS=.example.com" "and never asked for"
fi

if test_case "one unusable name does not stop a good one beside it renewing"; then
    renew 'homespool.example.com;bad$name;other.example.com'
    assert_contains "$(asked)" "LEGO_DOMAINS=homespool.example.com" "the first good name renews"
    assert_contains "$(asked)" "LEGO_DOMAINS=other.example.com" "and so does the one after the bad entry"
    assert_not_contains "$(asked)" 'LEGO_DOMAINS=bad$name' "the bad entry is skipped"
    assert_status "$status" 1 "and the unit still fails"
fi

if test_case "a glob does not become the names of the files beside the compose file"; then
    # Pathname expansion applies to the unquoted split, so without -f this asks a certificate
    # authority for a certificate for compose.yaml.
    renew "*"
    assert_not_contains "$(asked)" "LEGO_DOMAINS=compose.yaml" "the compose file is not a domain name"
    assert_not_contains "$(asked)" "LEGO_DOMAINS=secret.pem" "nor is anything else in the directory"
    assert_contains "$out" "not a usable hostname" "the glob itself is refused, unexpanded"
fi

if test_case "a stray space around a semicolon is trimmed rather than refused"; then
    renew " homespool.example.com ; other.example.com "
    assert_status "$status" 0 "a typo in spacing is not a failure"
    assert_contains "$(asked)" "LEGO_DOMAINS=homespool.example.com" "the first name survives the trim"
    assert_contains "$(asked)" "LEGO_DOMAINS=other.example.com" "and so does the second"
fi

if test_case "an empty ACME_HOSTS is not a fault"; then
    renew ""
    assert_status "$status" 0 "a deployment with no public name is a complete configuration"
    assert_contains "$out" "nothing to renew" "and is told so"
    assert_not_contains "$(asked)" "LEGO_DOMAINS" "with nothing asked of any authority"
fi

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed assertions passed."
else
    echo "$failed failed, $passed passed."
    exit 1
fi
