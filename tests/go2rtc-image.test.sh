#!/usr/bin/env bash
#
# Tests that the camera sidecar's image carries every fix it is meant to: each patch under
# go2rtc/patches is copied into the build and applied, and go2rtc/ holds nothing else unaccounted for.
#
#   tests/go2rtc-image.test.sh              # run them all
#   tests/go2rtc-image.test.sh patch        # run only tests whose name contains "patch"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# WHY THIS EXISTS. go2rtc/Dockerfile names each patch rather than applying the directory, so adding
# one is a step that cannot be skipped by a glob that matched nothing. The cost is a list that can
# drift: a patch dropped into the directory with no COPY and no git apply is a fix that is simply
# absent from the image, and go2rtc builds and starts happily without it. Reading the Dockerfile, not
# building it, so no daemon and no network are needed.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
go2rtc_dir="$repo_root/go2rtc"
dockerfile="$go2rtc_dir/Dockerfile"
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

# Every source path a COPY names, one per line, leaving out the build stage's output: the flags
# dropped, then every word but the last, which is the destination.
copy_sources() {
    grep '^COPY ' "$dockerfile" | grep -v -- '--from=' | awk '{
        n = 0
        for (i = 2; i <= NF; i++) if ($i !~ /^--/) words[++n] = $i
        for (i = 1; i < n; i++) print words[i]
        delete words
    }'
}

if test_case "every patch is copied into the build and applied"; then
    sources="$(copy_sources)"
    checked=0
    while IFS= read -r patch; do
        checked=$((checked + 1))
        if grep -qxF "patches/$patch" <<<"$sources"; then pass; else
            fail "go2rtc/patches/$patch is in the directory but no COPY names it, so the build never sees it"
        fi
        if grep -qE "^RUN git apply .*/patches/${patch//./\\.}\$" "$dockerfile"; then pass; else
            fail "go2rtc/patches/$patch is copied but no RUN git apply names it, so the image goes without it"
        fi
    done < <(cd "$go2rtc_dir/patches" && find . -type f | sed 's|^\./||' | sort)

    # One today. Zero would mean the listing broke, not that everything is covered.
    if [ "$checked" -lt 1 ]; then
        fail "checked $checked patches; expected at least 1, so the listing is not seeing them"
    fi
fi

if test_case "every COPY names a file that exists"; then
    while IFS= read -r source; do
        if [ -f "$go2rtc_dir/$source" ]; then pass; else
            fail "a COPY names $source, which is not in go2rtc/ - the image build would fail"
        fi
    done < <(copy_sources)
fi

if test_case "go2rtc/ holds only the Dockerfile and its patches"; then
    # Anything else is a file somebody meant the image to use, which nothing here would ever copy.
    stray="$(cd "$go2rtc_dir" && find . -type f ! -name Dockerfile ! -path './patches/*' | sed 's|^\./||')"
    if [ -z "$stray" ]; then pass; else fail "files no step uses: $stray"; fi
fi

if test_case "the release is pinned by a full commit as well as its tag"; then
    # A tag can be moved; the build refuses a clone whose HEAD is not this commit.
    if grep -qE '^ARG GO2RTC_COMMIT=[0-9a-f]{40}$' "$dockerfile"; then pass; else
        fail "no 'ARG GO2RTC_COMMIT=' with a full 40-character commit"
    fi
    if grep -qE '^ARG GO2RTC_VERSION=v[0-9]+\.[0-9]+\.[0-9]+$' "$dockerfile"; then pass; else
        fail "no 'ARG GO2RTC_VERSION=' naming a release tag"
    fi
fi

if test_case "the toolchain and the Go modules are built on as resolved, and recorded"; then
    # Both are compiled into the binary, so a floating choice made at build time has to be one the
    # image can name afterwards: ./build.sh resolves them, and these lines are what carry the answer
    # from there into the build and into the labels.
    compose="$repo_root/compose.yaml"
    checks=(
        '^ARG HOMESPOOL_BUILDER_IMAGE=[^[:space:]]+$|the builder is named where tools/base-digest.sh reads it'
        '^ARG HOMESPOOL_BUILDER_DIGEST=$|with a digest beside it'
        '^FROM --platform=\$BUILDPLATFORM \$\{HOMESPOOL_BUILDER_IMAGE\}\$\{HOMESPOOL_BUILDER_DIGEST:\+@\$\{HOMESPOOL_BUILDER_DIGEST\}\} AS build$|and the build stage runs on exactly that'
        '^ARG GO2RTC_UPDATED_MODULES="[^"]+"$|the modules are listed where tools/go-module-versions.sh reads them'
        '^RUN go get \$\{GO2RTC_MODULE_VERSIONS:-|and go get takes the resolved versions'
        'io\.github\.henrikottesorensen\.homespool\.builder\.digest="\$\{HOMESPOOL_BUILDER_DIGEST\}"|the builder digest is recorded'
        'io\.github\.henrikottesorensen\.homespool\.go\.modules="\$\{GO2RTC_MODULE_VERSIONS\}"|and the module versions'
    )
    for check in "${checks[@]}"; do
        if grep -qE "${check%%|*}" "$dockerfile"; then pass; else fail "go2rtc/Dockerfile: ${check#*|}"; fi
    done

    for arg in 'HOMESPOOL_BUILDER_DIGEST: "${HOMESPOOL_GOLANG_DIGEST:-}"' 'GO2RTC_MODULE_VERSIONS: "${HOMESPOOL_GO_MODULES:-}"'; do
        if grep -qF "$arg" "$compose"; then pass; else fail "compose.yaml does not pass $arg to the build"; fi
    done
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
