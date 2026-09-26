#!/usr/bin/env bash
#
# Prints the release version a build should be labelled with, and nothing else:
#
#   0.1          # HEAD carries the tag v0.1 and the working tree matches it
#                # nothing, for every other build
#
# A version is a promise about exactly one tree, so it is only claimed for exactly that tree: HEAD
# must carry a v-tag itself - a commit after the release is not the release - and nothing may differ
# from HEAD, by the same test tools/gitref.sh uses for its .dirty marker. The leading v is dropped,
# because the label and the image tag carry the version, not the name of the git tag.
#
# Should a commit carry more than one v-tag, git describe picks one of them; tag releases one per
# commit.
#
# PRINTS NOTHING, rather than failing, when there is no repository, no git or no such tag: most
# builds are not releases, and a caller uses this under `set -e` in a command substitution.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

command -v git >/dev/null 2>&1 || exit 0
git -C "$repo_root" rev-parse --git-dir >/dev/null 2>&1 || exit 0

[ -z "$(git -C "$repo_root" status --porcelain 2>/dev/null)" ] || exit 0

tag="$(git -C "$repo_root" describe --exact-match --tags --match 'v[0-9]*' HEAD 2>/dev/null)" || exit 0
[ -n "$tag" ] || exit 0

printf '%s\n' "${tag#v}"
