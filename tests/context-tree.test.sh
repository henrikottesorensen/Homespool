#!/usr/bin/env bash
#
# Tests for tools/context-tree.sh - that it prints the git tree a build context is at, and nothing
# when no tree describes it - and for the wiring that carries each tree into its image's labels.
#
#   tests/context-tree.test.sh             # run them all
#   tests/context-tree.test.sh wiring      # run only tests whose name contains "wiring"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# WHY THIS EXISTS. The update check on a host calls a published image the running one under a later
# commit when their trees and other inputs match. A tree printed for a context with uncommitted
# changes would claim two different images are the same, and a context path or variable crossed
# between two services would compare the proxy's tree with the sidecar's. The tool runs against a
# scratch repository; the wiring is read, not built, so no daemon is needed.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
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
    scratch="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/context-tree.XXXXXX")" && pwd)"
    return 0
}

# A scratch repository with the tool in it, two contexts and an ignored file pattern, committed.
repository() {
    mkdir -p "$scratch/repo/tools" "$scratch/repo/one" "$scratch/repo/two"
    cp "$repo_root/tools/context-tree.sh" "$scratch/repo/tools/"
    printf 'one\n' > "$scratch/repo/one/file"
    printf 'two\n' > "$scratch/repo/two/file"
    printf '*.ignored\n' > "$scratch/repo/.gitignore"
    git -C "$scratch/repo" init -q
    git -C "$scratch/repo" add -A
    git -C "$scratch/repo" -c user.name=test -c user.email=test@example.net commit -qm initial
}

tree() {
    output="$("$scratch/repo/tools/context-tree.sh" "$1" 2> "$scratch/stderr")"
    status=$?
}

if test_case "a clean context prints its tree, and the repository root the root tree"; then
    repository
    tree one
    assert_status "$status" 0 "succeeds"
    assert_equals "$output" "$(git -C "$scratch/repo" rev-parse HEAD:one)" "the context's own tree"
    tree .
    assert_equals "$output" "$(git -C "$scratch/repo" rev-parse 'HEAD^{tree}')" "the root's"
fi

if test_case "a context with an untracked, an edited or a staged file prints nothing, and another context is unaffected"; then
    repository
    printf 'new\n' > "$scratch/repo/one/untracked"
    tree one
    assert_status "$status" 0 "an untracked file: still succeeds"
    assert_equals "$output" "" "and prints nothing"
    tree two
    assert_equals "$output" "$(git -C "$scratch/repo" rev-parse HEAD:two)" "the other context still has its tree"
    tree .
    assert_equals "$output" "" "and the root, which holds the change, has none"

    rm "$scratch/repo/one/untracked"
    printf 'edited\n' > "$scratch/repo/one/file"
    tree one
    assert_equals "$output" "" "an edited file"
    git -C "$scratch/repo" add one/file
    tree one
    assert_equals "$output" "" "a staged one"
fi

if test_case "an ignored file is no change, as git sees it"; then
    repository
    printf 'build output\n' > "$scratch/repo/one/out.ignored"
    tree one
    assert_equals "$output" "$(git -C "$scratch/repo" rev-parse HEAD:one)" "the tree is still printed"
fi

if test_case "a path the commit does not have prints nothing and succeeds"; then
    repository
    tree nowhere
    assert_status "$status" 0 "succeeds"
    assert_equals "$output" "" "and prints nothing"
fi

if test_case "outside a repository it prints nothing and succeeds"; then
    mkdir -p "$scratch/tarball/tools"
    cp "$repo_root/tools/context-tree.sh" "$scratch/tarball/tools/"
    output="$(GIT_CEILING_DIRECTORIES="$scratch" "$scratch/tarball/tools/context-tree.sh" . 2>&1)"
    status=$?
    assert_status "$status" 0 "succeeds"
    assert_equals "$output" "" "and prints nothing"
fi

# service|build context in compose.yaml|the variable build.sh fills|the Dockerfile
wiring=(
    "homespool|.|HOMESPOOL_APP_TREE|Homespool.Host/Dockerfile"
    "proxy|./nginx|HOMESPOOL_PROXY_TREE|nginx/Dockerfile"
    "go2rtc|./go2rtc|HOMESPOOL_GO2RTC_TREE|go2rtc/Dockerfile"
)

if test_case "wiring: each service's context path and tree reach its labels"; then
    compose="$repo_root/compose.yaml"
    for row in "${wiring[@]}"; do
        IFS='|' read -r service context variable dockerfile <<< "$row"
        path="${context#./}"
        # The service's build block: from its context line to the blank line that ends its args.
        block="$(awk -v c="      context: $context" '$0 == c { on = 1 } on && /^$/ { exit } on' "$compose")"
        if [ -z "$block" ]; then fail "$service: no 'context: $context' in compose.yaml"; continue; fi
        if grep -qF "HOMESPOOL_CONTEXT_PATH: \"$path\"" <<< "$block"; then passed=$((passed + 1)); else
            fail "$service: compose.yaml passes HOMESPOOL_CONTEXT_PATH \"$path\", its context:"
        fi
        if grep -qF "HOMESPOOL_CONTEXT_TREE: \"\${$variable:-}\"" <<< "$block"; then passed=$((passed + 1)); else
            fail "$service: compose.yaml passes \${$variable:-} as HOMESPOOL_CONTEXT_TREE"
        fi
        for builder in build.sh pi/build.sh; do
            if grep -qF "$variable=\"\$(\"\$repo_root/tools/context-tree.sh\" $path)\"" "$repo_root/$builder"; then
                passed=$((passed + 1))
            else
                fail "$service: $builder fills $variable from tools/context-tree.sh $path"
            fi
        done
        for key in path tree; do
            upper="$(tr '[:lower:]' '[:upper:]' <<< "$key")"
            if grep -qF "io.github.henrikottesorensen.homespool.context.$key=\"\${HOMESPOOL_CONTEXT_$upper}\"" \
                "$repo_root/$dockerfile"; then
                passed=$((passed + 1))
            else
                fail "$service: $dockerfile labels context.$key"
            fi
        done
    done
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
