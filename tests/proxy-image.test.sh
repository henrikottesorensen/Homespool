#!/usr/bin/env bash
#
# Tests that the proxy image carries the whole proxy: every file under nginx/ is named by a COPY in
# nginx/Dockerfile, and compose.yaml mounts nothing into /etc/nginx but the two certificate volumes.
#
#   tests/proxy-image.test.sh              # run them all
#   tests/proxy-image.test.sh compose      # run only tests whose name contains "compose"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# WHY THIS EXISTS. The Dockerfile names each file rather than copying the directory, so where it lands
# is stated beside the reason for it - conf.d, the templates directory, or beside conf.d for a hook to
# install on a condition. The cost is a list that can drift: a file added to nginx/ with no COPY is a
# listener or a setting that is simply absent from the image, and nginx starts happily without it.
# Reading the Dockerfile, not building it, so no daemon is needed.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
nginx_dir="$repo_root/nginx"
dockerfile="$nginx_dir/Dockerfile"
compose="$repo_root/compose.yaml"
filter="${1:-}"

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

# Every source path a COPY names, one per line: the flags dropped, then every word but the last,
# which is the destination.
copy_sources() {
    grep '^COPY ' "$dockerfile" | awk '{
        n = 0
        for (i = 2; i <= NF; i++) if ($i !~ /^--/) words[++n] = $i
        for (i = 1; i < n; i++) print words[i]
        delete words
    }'
}

if test_case "every file in the build context is copied into the image"; then
    sources="$(copy_sources)"
    checked=0
    while IFS= read -r file; do
        checked=$((checked + 1))
        if grep -qxF "$file" <<<"$sources"; then pass; else
            fail "nginx/$file is in the build context but no COPY names it, so the image does not carry it"
        fi
    done < <(cd "$nginx_dir" && find . -type f ! -name Dockerfile | sed 's|^\./||' | sort)

    # Sixteen today. Zero would mean the listing broke, not that everything is covered.
    if [ "$checked" -lt 16 ]; then
        fail "checked $checked files; expected at least 16, so the listing is not seeing them"
    fi
fi

if test_case "every COPY names a file that exists"; then
    while IFS= read -r source; do
        if [ -f "$nginx_dir/$source" ]; then pass; else
            fail "a COPY names $source, which is not in nginx/ - the image build would fail"
        fi
    done < <(copy_sources)
fi

if test_case "compose.yaml mounts nothing into /etc/nginx but the two certificate volumes"; then
    # The configuration is the image's. A mount over one of its files would quietly replace it with
    # whatever the host holds, which is the drift the image exists to prevent.
    mounts="$(grep -E '^[[:space:]]*-[[:space:]].*:/etc/nginx' "$compose" || true)"
    unexpected="$(grep -vE 'homespool-proxy-certs:/etc/nginx/certs$|homespool-printer-leaf:/etc/nginx/printer-certs:ro$' <<<"$mounts" || true)"
    if [ -z "$unexpected" ]; then pass; else fail "a mount into /etc/nginx: $unexpected"; fi
    if [ "$(grep -c . <<<"$mounts")" -eq 2 ]; then pass; else
        fail "expected the two certificate volumes, found: ${mounts:-nothing}"
    fi
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
