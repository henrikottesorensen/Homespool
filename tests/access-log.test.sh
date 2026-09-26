#!/usr/bin/env bash
#
# Tests for nginx/homespool-access-log.conf - that every server block the proxy can serve names the
# format that leaves query strings out.
#
#   tests/access-log.test.sh              # run them all
#   tests/access-log.test.sh image        # run only tests whose name contains "image"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# WHAT IS UNDER TEST IS COVERAGE, not nginx. The format only applies where a server block names it:
# the base image's nginx.conf logs every request in full at the http level, and a block without its
# own access_log inherits that - silently, since nginx starts and logs as happily either way. So the
# failure this file exists to catch is a server block added later, or a file renamed out of the
# COPY that sorts the format first, and both look fine until somebody reads the log.
#
# What the format does to a request was checked against the real image rather than here: it needs
# nginx, and every other test in this directory runs without a daemon.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
nginx_dir="$repo_root/nginx"
dockerfile="$nginx_dir/Dockerfile"
filter="${1:-}"

directive='access_log /var/log/nginx/access.log homespool;'

passed=0
failed=0
current=""

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $current"
    echo "        $1"
}

pass() { passed=$((passed + 1)); }

test_case() {
    current="$1"
    [ -z "$filter" ] || [[ "$1" == *"$filter"* ]]
}

# Every top-level server block in a file, one per record, separated by NUL. Brace depth is counted
# from the first `server {` at depth zero until it closes; comments are dropped first so a brace in
# prose cannot move the count.
server_blocks() {
    sed 's/#.*$//' "$1" | awk '
        depth == 0 && /^[[:space:]]*server[[:space:]]*\{/ { inside = 1; block = "" }
        inside {
            block = block $0 "\n"
            opens = gsub(/\{/, "{"); closes = gsub(/\}/, "}")
            depth += opens - closes
            if (depth == 0) { printf "%s%c", block, 0; inside = 0 }
        }'
}

if test_case "every server block in a config file names the format"; then
    found=0
    for file in "$nginx_dir"/*.conf "$nginx_dir"/*.template; do
        while IFS= read -r -d '' block; do
            found=$((found + 1))
            case "$block" in
                *"$directive"*) pass ;;
                *) fail "$(basename "$file"): a server block without its own access_log inherits the base image's, which writes query strings: $(printf '%s' "$block" | grep -m1 listen)" ;;
            esac
        done < <(server_blocks "$file")
    done

    # Six today: three in the main template, one each for the printer, legacy printer and transfer
    # listeners. A count of zero would mean the parser stopped finding blocks, not that all is well.
    if [ "$found" -lt 6 ]; then
        fail "found $found server blocks; expected at least 6, so the parser is not seeing them"
    fi
fi

if test_case "the generated TLS blocks get the format through the body they include"; then
    # 26-user-tls-servers.sh writes one block per name whose only contents beyond the listener and
    # certificate are `include $BODY`, so the body is where the directive has to be.
    if grep -q "include \$BODY;" "$nginx_dir/docker-entrypoint.d/26-user-tls-servers.sh"; then pass; else
        fail "the generator no longer includes the body - check where its blocks get their access_log"
    fi
    if grep -qxF "$directive" "$nginx_dir/homespool-user-tls.conf"; then pass; else
        fail "homespool-user-tls.conf does not name the format at server level"
    fi
fi

if test_case "no server block names any other format"; then
    others="$(grep -h "^[[:space:]]*access_log" "$nginx_dir"/*.conf "$nginx_dir"/*.template | grep -vF "$directive" || true)"
    if [ -z "$others" ]; then pass; else fail "another access_log: $others"; fi
fi

if test_case "the format leaves the query out of the request and the referrer"; then
    format="$nginx_dir/homespool-access-log.conf"
    if grep -q '"$request"\|"$http_referer"' "$format"; then
        fail "the log_format names \$request or \$http_referer directly, which carry the query"
    else
        pass
    fi
fi

if test_case "the proxy image installs the format into conf.d, sorting first"; then
    # A format is looked up when an access_log naming it is parsed, and conf.d is included in sorted
    # order - so the destination is part of the fix, not a detail of it.
    copy="$(grep "homespool-access-log.conf" "$dockerfile" | grep '^COPY' || true)"
    case "$copy" in
        *" /etc/nginx/conf.d/00-"*) pass ;;
        *) fail "expected a COPY to /etc/nginx/conf.d/00-*, found: ${copy:-nothing}" ;;
    esac
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
