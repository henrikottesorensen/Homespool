#!/usr/bin/env bash
#
# Tests for tools/base-digest.sh - that it prints the digest a Dockerfile's base tag points at and
# nothing else, and that it fails rather than print anything that is not a digest.
#
#   tests/base-digest.test.sh             # run them all
#   tests/base-digest.test.sh refuses     # run only tests whose name contains "refuses"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# docker is a stub on PATH that records every call and answers the registry read the way buildx
# does: the digest for a template it executes, and its human-readable summary for a format starting
# {{.Manifest, which buildx before 0.33.0 prints instead of executing it. Nothing reaches a daemon or
# a registry.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/tools/base-digest.sh"
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
    scratch="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/base-digest.XXXXXX")" && pwd)"
    mkdir -p "$scratch/bin"

    cat > "$scratch/bin/docker" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$STUB_LOG"
# buildx imagetools inspect --format <format> <reference>
[ -n "${STUB_REGISTRY_FAILS:-}" ] && { echo "ERROR: not found" >&2; exit 1; }
case "$5" in
    '{{.Manifest'*)
        printf 'Name:      %s\nMediaType: application/vnd.oci.image.index.v1+json\nDigest:    %s\n' \
            "$6" "${STUB_DIGEST:-sha256:base}"
        ;;
    *) printf '%s\n' "${STUB_DIGEST:-sha256:base}" ;;
esac
STUB
    chmod 755 "$scratch/bin/docker"

    export PATH="$scratch/bin:$PATH" STUB_LOG="$scratch/docker.log"
    unset STUB_DIGEST STUB_REGISTRY_FAILS
    : > "$STUB_LOG"
    return 0
}

dockerfile() {
    printf '%s\n' "$@" > "$scratch/Dockerfile"
}

run() {
    output="$("$script" "$@" 2> "$scratch/stderr")"
    status=$?
    stderr="$(cat "$scratch/stderr")"
    log="$(cat "$STUB_LOG")"
}

if test_case "prints the digest the base tag points at, and nothing else"; then
    dockerfile 'ARG HOMESPOOL_BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0' 'FROM ${HOMESPOOL_BASE_IMAGE}'
    export STUB_DIGEST="sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f"
    run "$scratch/Dockerfile"
    assert_status "$status" 0 "resolves"
    assert_equals "$output" "$STUB_DIGEST" "the digest alone, as buildx before 0.33.0 prints it too"
    assert_contains "$log" " mcr.microsoft.com/dotnet/aspnet:10.0" "the registry is asked for the Dockerfile's tag"
fi

if test_case "a second argument names another ARG"; then
    dockerfile 'ARG HOMESPOOL_BASE_IMAGE=alpine:3' 'ARG HOMESPOOL_BUILDER_IMAGE=golang:1.25-alpine'
    run "$scratch/Dockerfile" HOMESPOOL_BUILDER_IMAGE
    assert_status "$status" 0 "resolves"
    assert_equals "$output" "sha256:base" "the builder's digest"
    assert_contains "$log" " golang:1.25-alpine" "the registry is asked for the builder's tag"
fi

if test_case "refuses a Dockerfile with no such ARG, without asking the registry"; then
    dockerfile 'FROM alpine:3'
    run "$scratch/Dockerfile"
    assert_status "$status" 1 "fails"
    assert_equals "$output" "" "prints nothing a build could take for a digest"
    assert_contains "$stderr" "no 'ARG HOMESPOOL_BASE_IMAGE=' line" "says which line is missing"
    assert_equals "$log" "" "the registry is not asked"
fi

if test_case "refuses when the registry cannot resolve the tag"; then
    dockerfile 'ARG HOMESPOOL_BASE_IMAGE=alpine:3'
    export STUB_REGISTRY_FAILS=1
    run "$scratch/Dockerfile"
    assert_status "$status" 1 "fails"
    assert_equals "$output" "" "prints nothing a build could take for a digest"
fi

if test_case "refuses an answer that is not a digest"; then
    dockerfile 'ARG HOMESPOOL_BASE_IMAGE=alpine:3'
    export STUB_DIGEST="alpine:3"
    run "$scratch/Dockerfile"
    assert_status "$status" 1 "fails"
    assert_equals "$output" "" "prints nothing a build could take for a digest"
    assert_contains "$stderr" "could not resolve alpine:3 to a digest" "says what it could not resolve"
fi

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]
