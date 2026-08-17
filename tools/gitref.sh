#!/usr/bin/env bash
#
# Prints the commit a build should be stamped with, and nothing else:
#
#   dd029e0fa7a6a29b699dc1d7f3b414ae2903ee38
#   dd029e0fa7a6a29b699dc1d7f3b414ae2903ee38.dirty    # working tree differs from HEAD
#
# This exists because a container build cannot work it out for itself. SourceLink, inside the SDK,
# stamps the commit into every assembly during compilation - but .dockerignore excludes **/.git, so
# inside the image there is no repository to read, the stamp is silently omitted, and the deployed
# artefact becomes the one build that cannot say what it is. So the value is computed out here and
# passed in; see Homespool.Host/BuildInformation.cs.
#
# One definition, three callers - build.sh, pi/build.sh and the CI workflow - because the alternative
# was the same two git commands written three times and drifting.
#
# THE ".dirty" MARKER IS ONLY EVER APPLIED HERE, never at compile time, and that is deliberate. Any
# difference from HEAD counts, which is the right rule for something producing a distributable
# artefact and the wrong one for a local `dotnet build`: roughly a fifth of this repository's commits
# touch no compile input at all, so a compile-time check would report an edit to a README or
# setup-env.sh as a modified binary. Note the consequence, which is the price of not having that
# noise: a binary you build locally from an edited tree reports a bare commit with no marker.
#
# Accuracy of the marker rests entirely on .gitignore, because untracked files count - correctly, a
# new .cs file is untracked and does change the build. Anything that lands in the tree during a build
# and is not ignored would make every build report modified.
#
# PRINTS NOTHING when there is no repository or no git, rather than failing: a source tarball
# genuinely has no commit, and that is a legitimate state the reader reports as unknown. Exits zero
# either way, so a caller can use it under `set -e` in a command substitution.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

command -v git >/dev/null 2>&1 || exit 0

# --git-dir rather than --is-inside-work-tree: this must be a repository we can actually read HEAD
# from, and it has to hold for a worktree, where .git is a FILE pointing elsewhere rather than a
# directory. All Homespool work happens in worktrees, so that is the normal case here, not the edge.
git -C "$repo_root" rev-parse --git-dir >/dev/null 2>&1 || exit 0

sha="$(git -C "$repo_root" rev-parse HEAD 2>/dev/null)" || exit 0
[ -n "$sha" ] || exit 0

if [ -n "$(git -C "$repo_root" status --porcelain 2>/dev/null)" ]; then
    printf '%s.dirty\n' "$sha"
else
    printf '%s\n' "$sha"
fi
