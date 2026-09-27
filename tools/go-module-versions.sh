#!/usr/bin/env bash
#
# Prints the latest release of each Go module a Dockerfile takes newer than its source pins, as the
# `module@version` list `go get` accepts, and nothing else:
#
#   tools/go-module-versions.sh go2rtc/Dockerfile
#   golang.org/x/crypto@v0.57.0 golang.org/x/net@v0.59.0
#
# The camera sidecar takes these at their latest so their fixes arrive without an edit here - and a
# build that asks for @latest cannot say afterwards what it got. So the build scripts resolve the list
# first and pass it in: the image is built with exactly those versions and records them, the way
# tools/base-digest.sh does for a base image.
#
# The modules are read from the Dockerfile's own `ARG GO2RTC_UPDATED_MODULES="..."` line, so the
# Dockerfile stays the one place they are named. "Latest" is what the module proxy answers for
# @latest - the highest tagged release, never a commit - which is what `go get module@latest` would
# have taken from the same proxy.
#
# FAILS when any module cannot be resolved, rather than print part of the list: the build would then
# take the missing one at whatever @latest meant at that moment, while the image claimed a full list.
# It needs the network, as the build it feeds does.
set -euo pipefail

dockerfile="${1:?usage: $0 <Dockerfile>}"
proxy="${GOPROXY_URL:-https://proxy.golang.org}"

die() {
    echo "$0: $*" >&2
    exit 1
}

modules="$(sed -n 's/^ARG GO2RTC_UPDATED_MODULES="\(.*\)"$/\1/p' "$dockerfile")"
[ -n "$modules" ] || die "no 'ARG GO2RTC_UPDATED_MODULES=\"...\"' line in $dockerfile"

resolved=()
for module in $modules; do
    # The proxy protocol escapes capital letters in a module path; none of ours has any, and a path
    # that did would be asked for wrongly rather than refused, so it is refused.
    case "$module" in
        *[A-Z]*) die "$module has capital letters, which the module proxy needs escaped" ;;
    esac

    answer="$(curl -fsS "$proxy/$module/@latest")" || die "could not ask $proxy for $module"
    version="$(printf '%s' "$answer" | sed -n 's/.*"Version":"\([^"]*\)".*/\1/p')"

    case "$version" in
        v[0-9]*) resolved+=("$module@$version") ;;
        *) die "$proxy answered no version for $module (got '$answer')" ;;
    esac
done

printf '%s\n' "${resolved[*]}"
