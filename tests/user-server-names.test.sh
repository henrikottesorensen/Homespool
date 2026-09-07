#!/usr/bin/env bash
#
# Tests for nginx/docker-entrypoint.d/16-user-server-names.envsh.
#
#   tests/user-server-names.test.sh              # run them all
#   tests/user-server-names.test.sh refuse       # run only tests whose name contains "refuse"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# The script is SOURCED by the base image's entrypoint, so each case sources it the same way, in a
# fresh shell with USER_HOSTS set and nothing else - which is also what keeps one case's exports out
# of the next.
#
# WHICH SHELL, AND WHY IT IS NOT PLAIN `sh`. The script under test runs in the nginx image, where
# /bin/sh is dash, so dash is the closest thing to the real parser and is preferred when present.
# The fallback is bash - but NOT macOS's /bin/sh, which is bash 3.2, and bash 3.2 cannot parse a
# `case` inside a command substitution at all. That is a bug in that shell rather than anything about
# this file: the same failure happens on the version of the script that shipped, and both parse
# cleanly under dash and under bash 5. Left unhandled it makes this suite fail on a stock Mac and
# pass everywhere else, which would say nothing about the script.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/nginx/docker-entrypoint.d/16-user-server-names.envsh"
filter="${1:-}"

# dash if there is one, else a bash that is not 3.2. See the note above. A machine with neither
# SKIPS rather than fails: the shell would be the thing under test, and it is not the thing under
# test. That is a stock Mac with no dash installed, and it exits 0 so a suite runner is not made to
# treat "cannot check this here" as "this is broken".
if command -v dash >/dev/null 2>&1; then
    posix_sh="$(command -v dash)"
elif [ "${BASH_VERSINFO[0]:-0}" -ge 4 ]; then
    posix_sh="$BASH"
else
    echo "SKIP  tests/user-server-names.test.sh: needs dash or bash 4+ to source the script under"
    echo "      test - bash 3.2 cannot parse a case inside a command substitution, which the"
    echo "      shipped script has and the nginx image's dash reads without complaint."
    exit 0
fi

passed=0
failed=0
current=""

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $current"
    echo "        $1"
    [ $# -gt 1 ] && printf '        expected: %s\n        actual:   %s\n' "$2" "$3"
}

assert_eq() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "${3:-values differ}" "$1" "$2"
    fi
}

assert_contains() {
    case "$1" in
        *"$2"*) passed=$((passed + 1)) ;;
        *) fail "${3:-substring not found}" "…$2…" "$1" ;;
    esac
}

test_case() {
    current="$1"
    case "$current" in
        *"$filter"*) ;;
        *) current=""; return 1 ;;
    esac
    echo "- $current"
    return 0
}

# The exported name list for a given USER_HOSTS, with stderr discarded.
tls_names() {
    USER_HOSTS="$1" "$posix_sh" -c ". \"\$0\" 2>/dev/null; printf '%s' \"\$USER_TLS_NAMES\"" "$script"
}

# The spaced form, which is what the plain-HTTP server_name takes.
server_names() {
    USER_HOSTS="$1" "$posix_sh" -c ". \"\$0\" 2>/dev/null; printf '%s' \"\$USER_SERVER_NAMES\"" "$script"
}

# What it said while refusing, which is the half an operator sees.
complaints() {
    USER_HOSTS="$1" "$posix_sh" -c ". \"\$0\" >/dev/null" "$script" 2>&1
}

if test_case "the list is passed through with localhost folded in"; then
    assert_eq "home.lan;localhost" "$(tls_names 'home.lan')"
    assert_eq "home.lan localhost" "$(server_names 'home.lan')"
fi

if test_case "localhost is not repeated when it is already named"; then
    assert_eq "localhost" "$(tls_names 'localhost')" "a second localhost makes nginx warn at every start"
fi

if test_case "a name outside the hostname charset is refused"; then
    # A semicolon cannot arrive here - it is the separator - so the shape to check is one that
    # survives splitting: anything nginx would read as more than a name in server_name.
    #
    # A brace is the one that costs a deployment. Measured against the image built from the commit
    # before this check existed, with USER_HOSTS='good.lan;bad{name}': nginx refused to start at all,
    # with `directive "server_name" is not terminated by ";"` pointing at the GENERATED default.conf
    # rather than at .env - every listener down, printers included, over one character in a name.
    assert_eq "good.lan;localhost" "$(tls_names 'good.lan;bad{name}')"
    assert_eq "good.lan localhost" "$(server_names 'good.lan;bad{name}')"
fi

if test_case "a refused name is refused in both forms, not just the certificates"; then
    # The inconsistency this check exists for: 25 and 26 already refused it, so it had no certificate
    # and no TLS block, while the plain-HTTP server_name still announced it.
    output="$(server_names 'ok.lan;../etc/passwd')"
    assert_eq "ok.lan localhost" "$output" "a name the TLS half refuses must not survive in server_name"
fi

if test_case "a leading dot is refused"; then
    assert_eq "localhost" "$(tls_names '.lan')"
fi

if test_case "the refusal is said out loud"; then
    assert_contains "$(complaints 'good.lan;bad{name}')" "refusing bad{name}"
fi

if test_case "a stray space around a separator is still a name"; then
    assert_eq "one.lan;two.lan;localhost" "$(tls_names 'one.lan; two.lan')"
fi

if test_case "an empty USER_HOSTS falls back to localhost"; then
    assert_eq "localhost" "$(tls_names '')"
fi

echo
echo "shell:  $posix_sh"
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
