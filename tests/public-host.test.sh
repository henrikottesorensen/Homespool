#!/usr/bin/env bash
#
# Tests for nginx/homespool-public-host.conf.template, rendered the way the proxy image renders it.
#
#   tests/public-host.test.sh              # run them all
#   tests/public-host.test.sh suffix       # run only tests whose name contains "suffix"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# WHAT IS UNDER TEST IS THE SUBSTITUTION, not nginx. The map decides the Host the application is
# told about, and the application builds every emailed link from it - so the TLS arm has to come out
# carrying the port a browser should ask for, and the other two arms have to come out with their
# nginx variables intact. A filter that stopped naming REDIRECT_PORT_SUFFIX, or one widened to
# everything, breaks one of those without breaking the other, and both failures look like nginx
# misbehaving rather than like a rendering problem.
#
# The derivation is included rather than assumed: each case sets what an operator sets - HTTPS_PORT,
# or an explicit suffix - and the port suffix script runs first, exactly as the entrypoint runs it
# before the templates are rendered.
#
# WHICH SHELL, AND WHY IT IS NOT PLAIN `sh`. The derivation script is SOURCED by the base image's
# entrypoint, where /bin/sh is dash, so dash is the closest thing to the real parser and is
# preferred when present. The fallback is bash 4+, but NOT macOS's /bin/sh, which is bash 3.2 - see
# tests/user-server-names.test.sh, which makes the same choice for the same reason.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
template="$repo_root/nginx/homespool-public-host.conf.template"
derive="$repo_root/nginx/docker-entrypoint.d/15-redirect-port-suffix.envsh"
compose="$repo_root/compose.yaml"
dockerfile="$repo_root/nginx/Dockerfile"
filter="${1:-}"

# The variable list compose.yaml passes as NGINX_ENVSUBST_FILTER, in the form the base image's
# script builds from it. Without it envsubst replaces $host, $http_host and $server_port with
# nothing, which is the failure this file exists to catch.
filter_vars='${REDIRECT_PORT_SUFFIX} ${USER_SERVER_NAMES}'

if ! command -v envsubst >/dev/null 2>&1; then
    echo "SKIP  tests/public-host.test.sh: needs envsubst (GNU gettext) to render the template the"
    echo "      way the proxy image does."
    exit 0
fi

if command -v dash >/dev/null 2>&1; then
    posix_sh="$(command -v dash)"
elif [ "${BASH_VERSINFO[0]:-0}" -ge 4 ]; then
    posix_sh="$BASH"
else
    echo "SKIP  tests/public-host.test.sh: needs dash or bash 4+ to source the derivation script"
    echo "      under test."
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

# The rendered map, given an environment an operator's .env would produce. The derivation runs in
# the same shell as the render, which is what the entrypoint does: 15 exports, 20 substitutes.
render() {
    env "$@" "$posix_sh" -c \
        ". \"\$0\"; envsubst \"\$1\" < \"\$2\"" \
        "$derive" "$filter_vars" "$template"
}

# One arm of the map, by its key, with the whitespace between key and value collapsed so a case
# reads as the pair it is checking rather than as a column layout.
arm() {
    printf '%s' "$1" | awk -v key="$2" '$1 == key { $1 = ""; sub(/^ +/, ""); print }'
}

if test_case "the standard 443 deployment gets a clean name with no port"; then
    assert_eq '$host;' "$(arm "$(render HTTPS_PORT=443)" 8443)" \
        "a port on the majority deployment would be noise in every link it sends"
fi

if test_case "a published HTTPS port is derived into the suffix"; then
    assert_eq '$host:8443;' "$(arm "$(render HTTPS_PORT=8443)" 8443)"
fi

if test_case "an explicit suffix overrides the derivation"; then
    # The deployment behind a router that forwards public 9443 inward to some other port: what a
    # browser must ask for is not what Docker publishes, and only the operator knows that.
    assert_eq '$host:9443;' "$(arm "$(render HTTPS_PORT=8443 REDIRECT_PORT_SUFFIX=:9443)" 8443)"
fi

if test_case "an explicit empty suffix means 443, and is not treated as unset"; then
    # The other half of the same case: a router forwards public 443 inward to 8443, so the derived
    # ":8443" would name a port the router does not answer.
    assert_eq '$host;' "$(arm "$(render HTTPS_PORT=8443 REDIRECT_PORT_SUFFIX=)" 8443)"
fi

if test_case "the printer-side arm keeps the client's Host untouched"; then
    # Printers are told where to reach this server by the configured printer host and port, so
    # nothing on those listeners builds a URL from the request - and passing $http_host on is the
    # behaviour they already had.
    assert_eq '$http_host;' "$(arm "$(render HTTPS_PORT=8443)" default)" \
        "the filter blanked a variable the map needs, or the arm was changed"
fi

if test_case "the plain-HTTP arm keeps the name and takes no port"; then
    assert_eq '$host;' "$(arm "$(render HTTPS_PORT=8443)" 8080)"
fi

if test_case "the key stays the port nginx accepted on, not a published one"; then
    # $server_port must survive the render: it is what makes the map per-listener, and a blanked one
    # would make every arm match the first key instead.
    assert_contains "$(render HTTPS_PORT=8443)" 'map $server_port $hs_public_host {'
fi

if test_case "compose.yaml still names the variable the template needs"; then
    # Two things that must agree: a filter that stops naming REDIRECT_PORT_SUFFIX leaves the literal
    # in the rendered file, and nginx refuses to start on a variable nothing defines.
    assert_contains "$(grep NGINX_ENVSUBST_FILTER "$compose")" "REDIRECT_PORT_SUFFIX"
fi

if test_case "the proxy image puts the template where the entrypoint renders it"; then
    # Inert anywhere else: no file in the templates directory, no map in conf.d, and
    # homespool-proxy.conf then names a variable that does not exist.
    assert_contains "$(grep '^COPY.*homespool-public-host' "$dockerfile")" \
        "/etc/nginx/templates/00-public-host.conf.template"
fi

echo
echo "shell:  $posix_sh"
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
