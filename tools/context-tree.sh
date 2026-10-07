#!/usr/bin/env bash
#
# Prints the git tree an image's build context is at, and nothing else:
#
#   tools/context-tree.sh go2rtc     # 3e70bb2bb5a7772ee59d3085c06d34c2032668bf
#   tools/context-tree.sh .          # the whole repository's tree
#
# Every publish builds all three images and stamps each with the commit, so the camera sidecar gets a
# new digest when only the application changed. The tree is what says that its own source did not:
# two images with the same tree, base and other inputs differ in the stamp alone, and the update check
# on the host says so rather than offering the application's fixes as the sidecar's.
#
# PRINTS NOTHING when the context has changes git does not hold - edited, staged or untracked, under
# .gitignore's rules - because then no tree describes it, and a tree that did not would claim the
# image is the same as one it is not. Nothing too when there is no repository or no git, as
# tools/gitref.sh does. Exits zero either way, so a caller can use it under `set -e` in a command
# substitution.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
path="${1:?usage: $0 <build context, relative to the repository root>}"

command -v git >/dev/null 2>&1 || exit 0
git -C "$repo_root" rev-parse --git-dir >/dev/null 2>&1 || exit 0

[ -z "$(git -C "$repo_root" status --porcelain -- "$path" 2>/dev/null)" ] || exit 0

if [ "$path" = "." ]; then
    git -C "$repo_root" rev-parse --verify --quiet 'HEAD^{tree}' 2>/dev/null
else
    git -C "$repo_root" rev-parse --verify --quiet "HEAD:$path" 2>/dev/null
fi
exit 0
