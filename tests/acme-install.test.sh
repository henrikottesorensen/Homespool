#!/usr/bin/env bash
#
# Tests for acme/install.sh's refusal to install over a deployment that somebody outside the docker
# group could change - the timers run docker compose as root from it.
#
#   tests/acme-install.test.sh               # run them all
#   tests/acme-install.test.sh symlink       # run only tests whose name contains "symlink"
#
# No test framework, for the reason tests/setup-env.test.sh gives at length.
#
# The script is RUN, not sourced, under dash where there is one. It needs root and writes to /etc, so
# it runs on a PATH holding only what the ownership check uses: `id` and `getent` are stubs answering
# from fixture files, so who is in the docker group is decided by the case rather than by the machine,
# and there is no `systemctl`. A deployment the check accepts therefore stops at "No systemctl here",
# before anything is installed - that message is how a case tells acceptance from refusal.
#
# The files are real, in a scratch directory, owned by whoever runs this. Their modes are what each
# case changes; every account on the path above the scratch directory is written into the fixtures
# as a docker member, so the machine's own layout never decides a case.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/acme/install.sh"
filter="${1:-}"

if command -v dash >/dev/null 2>&1; then
    posix_sh="$(command -v dash)"
elif [ "${BASH_VERSINFO[0]:-0}" -ge 4 ]; then
    posix_sh="$BASH"
else
    echo "SKIP  tests/acme-install.test.sh: needs dash or bash 4+ to run the script under test."
    exit 0
fi

# GNU stat, because the script reads modes with `stat -c`. A Mac without coreutils has only BSD stat,
# and a check that cannot read a mode would fail every case for a reason unrelated to the script.
if ! stat -c %u / >/dev/null 2>&1 || ! realpath -e / >/dev/null 2>&1; then
    echo "SKIP  tests/acme-install.test.sh: needs GNU stat and realpath."
    exit 0
fi

me_uid="$(id -u)"

# Far from any gid a real machine uses, so no file in the scratch directory is in it by accident.
docker_gid=990000

passed=0
failed=0
scratch=""

cleanup() {
    # A case may leave a directory unwritable to its owner; put the write bit back before removing.
    [ -n "$scratch" ] && chmod -R u+w "$scratch" 2>/dev/null
    [ -n "$scratch" ] && rm -rf "$scratch"
}
trap cleanup EXIT

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $1"
    shift
    for line in "$@"; do echo "        $line"; done
}

assert_contains() {
    case "$1" in
        *"$2"*) passed=$((passed + 1)) ;;
        *) fail "$3" "expected to contain: $2" "actual: $1" ;;
    esac
}

assert_not_contains() {
    case "$1" in
        *"$2"*) fail "$3" "expected NOT to contain: $2" "actual: $1" ;;
        *) passed=$((passed + 1)) ;;
    esac
}

# The check accepted the deployment: the run got as far as looking for systemctl.
assert_accepted() {
    assert_not_contains "$out" "Refusing" "$1: not refused"
    assert_contains "$out" "No systemctl here" "$1: reaches the step after the check"
}

assert_equals() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "$3" "expected $2, got $1"
    fi
}

assert_refused() {
    assert_contains "$out" "Refusing to install" "$1: refused"
    assert_not_contains "$out" "Installing for" "$1: before anything is installed"
    assert_not_contains "$out" "No systemctl here" "$1: and stops at the check"
}

test_case() {
    case "$1" in
        *"$filter"*) ;;
        *) return 1 ;;
    esac
    echo "- $1"
    cleanup
    scratch="$(mktemp -d "${TMPDIR:-/tmp}/acme-install.XXXXXX")"
    scratch="$(cd -P "$scratch" && pwd -P)"
    mkdir -p "$scratch/bin" "$scratch/fixtures" "$scratch/deploy"
    chmod 0755 "$scratch" "$scratch/deploy"

    printf '# compose\n' > "$scratch/deploy/compose.yaml"
    printf 'X=1\n' > "$scratch/deploy/.env"
    chmod 0644 "$scratch/deploy/compose.yaml" "$scratch/deploy/.env"

    # The group the files really carry, which is not always the primary one: a directory with BSD
    # semantics hands its own group to what is created in it.
    me_gid="$(stat -c %g "$scratch/deploy/compose.yaml")"

    for tool in stat realpath readlink dirname cut tr; do
        ln -s "$(command -v "$tool")" "$scratch/bin/$tool"
    done

    # Builtins only: this PATH has nothing else on it.
    cat > "$scratch/bin/getent" <<'STUB'
#!/bin/sh
case "$1" in
    passwd) file="$STUB_PASSWD" ;;
    group) file="$STUB_GROUP" ;;
    *) exit 1 ;;
esac
while IFS= read -r line; do
    if [ $# -lt 2 ]; then
        printf '%s\n' "$line"
        continue
    fi
    name="${line%%:*}"
    rest="${line#*:}"
    rest="${rest#*:}"
    id="${rest%%:*}"
    if [ "$2" = "$name" ] || [ "$2" = "$id" ]; then
        printf '%s\n' "$line"
        exit 0
    fi
done < "$file"
[ $# -lt 2 ] || exit 2
STUB

    # `id -u` is root, so the script's own root check passes; `id -Gn -- NAME` is the account's
    # primary group and every group listing it, from the same fixtures getent reads.
    cat > "$scratch/bin/id" <<'STUB'
#!/bin/sh
case "$1" in
    -u) echo 0; exit 0 ;;
    -Gn) ;;
    *) exit 1 ;;
esac
shift
[ "$1" = -- ] && shift
who="$1"
primary=''
while IFS=: read -r name _ _ gid _; do
    [ "$name" = "$who" ] && primary="$gid"
done < "$STUB_PASSWD"
[ -n "$primary" ] || exit 1
groups=''
while IFS=: read -r gname _ gid members; do
    if [ "$gid" = "$primary" ]; then
        groups="$groups $gname"
        continue
    fi
    case ",$members," in
        *",$who,"*) groups="$groups $gname" ;;
    esac
done < "$STUB_GROUP"
echo $groups
STUB
    chmod +x "$scratch/bin/getent" "$scratch/bin/id"
    return 0
}

# Writes the fixtures. $1 is y when the account owning the scratch files is in the docker group;
# $2 is a comma-separated member list for that account's own group; $3 is extra passwd lines.
accounts() {
    local in_docker="$1" my_group_members="${2:-}" extra="${3:-}"
    local passwd="$scratch/fixtures/passwd" group="$scratch/fixtures/group"
    local docker_members='' path uid gid seen_uids=' ' seen_gids=' '

    printf 'me:x:%s:%s::/:/bin/sh\n' "$me_uid" "$me_gid" > "$passwd"
    [ "$in_docker" = y ] && docker_members=me

    # Every owner on the way up from the scratch directory, as a docker member, and every group as
    # one with nobody in it - so the machine's /tmp or home directory never decides a case.
    path="$(dirname "$scratch")"
    while :; do
        uid="$(stat -c %u "$path")"
        gid="$(stat -c %g "$path")"
        if [ "$uid" != "$me_uid" ] && [[ "$seen_uids" != *" $uid "* ]]; then
            printf 'owner%s:x:%s:%s::/:/bin/sh\n' "$uid" "$uid" "$docker_gid" >> "$passwd"
            seen_uids="$seen_uids$uid "
        fi
        if [ "$gid" != "$me_gid" ] && [[ "$seen_gids" != *" $gid "* ]]; then
            printf 'group%s:x:%s:\n' "$gid" "$gid" >> "$group"
            seen_gids="$seen_gids$gid "
        fi
        [ "$path" = / ] && break
        path="$(dirname "$path")"
    done

    printf 'me:x:%s:%s\n' "$me_gid" "$my_group_members" >> "$group"
    printf 'docker:x:%s:%s\n' "$docker_gid" "$docker_members" >> "$group"
    [ -n "$extra" ] && printf '%s\n' "$extra" >> "$passwd"
    return 0
}

install_over() {
    out="$(
        PATH="$scratch/bin" \
        STUB_PASSWD="$scratch/fixtures/passwd" \
        STUB_GROUP="$scratch/fixtures/group" \
            "$posix_sh" "$script" "${1:-$scratch/deploy}" 2>&1
    )"
    status=$?
}

if test_case "a deployment owned by a docker member is accepted"; then
    accounts y
    install_over
    assert_accepted "owner in docker"
fi

if test_case "a deployment owned by an account outside the docker group is refused"; then
    accounts n
    install_over
    assert_refused "owner outside docker"
    assert_contains "$out" "$scratch/deploy is owned by uid $me_uid" "the directory is named"
    assert_contains "$out" "$scratch/deploy/compose.yaml is owned by uid $me_uid" "and so is the compose file"
    assert_equals "$status" 1 "and the installer fails"
fi

if test_case "an owner with no account at all is refused"; then
    accounts y
    # Rewrite the owner's line away: a uid that names nobody is not one anybody vouched for.
    printf 'root:x:0:0::/:/bin/sh\n' > "$scratch/fixtures/passwd.new"
    while IFS= read -r line; do
        case "$line" in me:*) ;; *) printf '%s\n' "$line" >> "$scratch/fixtures/passwd.new" ;; esac
    done < "$scratch/fixtures/passwd"
    mv "$scratch/fixtures/passwd.new" "$scratch/fixtures/passwd"
    install_over
    assert_refused "unknown owner"
    assert_contains "$out" "is owned by uid $me_uid" "the uid is named"
fi

if test_case "a compose file anyone can write is refused"; then
    accounts y
    chmod 0666 "$scratch/deploy/compose.yaml"
    install_over
    assert_refused "world-writable compose.yaml"
    assert_contains "$out" "$scratch/deploy/compose.yaml is writable by every account" "and named"
fi

if test_case "an .env anyone can write is refused"; then
    accounts y
    chmod 0666 "$scratch/deploy/.env"
    install_over
    assert_refused "world-writable .env"
    assert_contains "$out" "$scratch/deploy/.env is writable by every account" "and named"
fi

if test_case "an override file compose would merge is checked"; then
    accounts y
    printf 'services: {}\n' > "$scratch/deploy/compose.override.yaml"
    chmod 0666 "$scratch/deploy/compose.override.yaml"
    install_over
    assert_refused "world-writable override"
    assert_contains "$out" "compose.override.yaml is writable by every account" "and named"
fi

if test_case "a checkout made under umask 0002 is accepted when its group holds only its owner"; then
    # The Raspberry Pi OS default: 664 and 775 under the owner's private group.
    accounts y
    chmod 0775 "$scratch/deploy"
    chmod 0664 "$scratch/deploy/compose.yaml" "$scratch/deploy/.env"
    install_over
    assert_accepted "user private group"
fi

if test_case "a group-writable file is refused when an account outside docker has that primary group"; then
    accounts y "" "mallory:x:990001:$me_gid::/:/bin/sh"
    chmod 0664 "$scratch/deploy/compose.yaml"
    install_over
    assert_refused "untrusted primary member"
    assert_contains "$out" "compose.yaml is writable by group $me_gid" "the group is named"
fi

if test_case "a group-writable file is refused when the group lists an account outside docker"; then
    accounts y "mallory" "mallory:x:990001:990001::/:/bin/sh"
    chmod 0664 "$scratch/deploy/compose.yaml"
    install_over
    assert_refused "untrusted listed member"
    assert_contains "$out" "compose.yaml is writable by group $me_gid" "the group is named"
fi

if test_case "a group-writable file is refused when its group names nobody"; then
    accounts y
    # Drop the owner's group from the fixture: a gid no group entry describes.
    grep -v "^me:" "$scratch/fixtures/group" > "$scratch/fixtures/group.new"
    mv "$scratch/fixtures/group.new" "$scratch/fixtures/group"
    chmod 0664 "$scratch/deploy/compose.yaml"
    install_over
    assert_refused "unnamed group"
    assert_contains "$out" "compose.yaml is writable by group $me_gid" "the group is named"
fi

if test_case "a directory anyone can write, above the deployment, is refused"; then
    accounts y
    mkdir "$scratch/open" && mv "$scratch/deploy" "$scratch/open/deploy"
    chmod 0777 "$scratch/open"
    install_over "$scratch/open/deploy"
    assert_refused "world-writable ancestor"
    assert_contains "$out" "$scratch/open is writable by every account" "the ancestor is named"
fi

if test_case "a sticky directory anyone can write, above the deployment, is accepted"; then
    # /tmp's shape. Nobody but an entry's owner can rename it out of a sticky directory.
    accounts y
    mkdir "$scratch/sticky" && mv "$scratch/deploy" "$scratch/sticky/deploy"
    chmod 1777 "$scratch/sticky"
    install_over "$scratch/sticky/deploy"
    assert_accepted "sticky ancestor"
fi

if test_case "a sticky deployment directory anyone can write is still refused"; then
    # Creating a file is exactly what the sticky bit allows, and an override file is a file.
    accounts y
    chmod 1777 "$scratch/deploy"
    install_over
    assert_refused "sticky deployment directory"
    assert_contains "$out" "$scratch/deploy is writable by every account" "and named"
fi

if test_case "a symlinked compose file is checked where it points"; then
    accounts y
    mkdir "$scratch/elsewhere"
    mv "$scratch/deploy/compose.yaml" "$scratch/elsewhere/compose.yaml"
    ln -s "$scratch/elsewhere/compose.yaml" "$scratch/deploy/compose.yaml"
    chmod 0777 "$scratch/elsewhere"
    install_over
    assert_refused "symlink into a world-writable directory"
    assert_contains "$out" "$scratch/elsewhere is writable by every account" "the target's directory is named"
fi

if test_case "a symlinked compose file is judged by its target's mode, not the link's"; then
    # A link's own mode is 0777 on Linux and 0755 on a Mac, and says nothing about who can write
    # what it points at.
    accounts y
    mkdir "$scratch/elsewhere"
    chmod 0755 "$scratch/elsewhere"
    mv "$scratch/deploy/compose.yaml" "$scratch/elsewhere/compose.yaml"
    chmod 0666 "$scratch/elsewhere/compose.yaml"
    ln -s "$scratch/elsewhere/compose.yaml" "$scratch/deploy/compose.yaml"
    install_over
    assert_refused "symlink to a world-writable file"
    assert_contains "$out" "$scratch/elsewhere/compose.yaml is writable by every account" "the target is named"
fi

if test_case "a symlinked compose file pointing somewhere sound is accepted"; then
    accounts y
    mkdir "$scratch/elsewhere"
    chmod 0755 "$scratch/elsewhere"
    mv "$scratch/deploy/compose.yaml" "$scratch/elsewhere/compose.yaml"
    ln -s "$scratch/elsewhere/compose.yaml" "$scratch/deploy/compose.yaml"
    install_over
    assert_accepted "symlink to a sound file"
fi

if test_case "a deployment reached through a symlink in a directory anyone can write is refused"; then
    # The units cd into the path as written, so whoever can replace the link chooses the directory.
    accounts y
    mkdir "$scratch/open"
    chmod 0777 "$scratch/open"
    ln -s "$scratch/deploy" "$scratch/open/homespool"
    install_over "$scratch/open/homespool"
    assert_refused "symlinked deployment in an open directory"
    assert_contains "$out" "$scratch/open is writable by every account" "the link's directory is named"
fi

if test_case "a deployment reached through a symlink in a sound directory is accepted"; then
    # On Linux a link's own mode is 0777, so this is the case that fails if a link is judged by it.
    accounts y
    mkdir "$scratch/opt"
    chmod 0755 "$scratch/opt"
    ln -s "$scratch/deploy" "$scratch/opt/homespool"
    install_over "$scratch/opt/homespool"
    assert_accepted "symlinked deployment"
fi

if test_case "a deployment linked from a sound directory into an open one is refused"; then
    # The path as written is sound; where it lands is not.
    accounts y
    mkdir "$scratch/opt" "$scratch/open"
    chmod 0755 "$scratch/opt"
    chmod 0777 "$scratch/open"
    mv "$scratch/deploy" "$scratch/open/deploy"
    ln -s "$scratch/open/deploy" "$scratch/opt/homespool"
    install_over "$scratch/opt/homespool"
    assert_refused "symlinked deployment landing in an open directory"
    assert_contains "$out" "$scratch/open is writable by every account" "the real directory's parent is named"
fi

if test_case "a deployment in an open directory is refused even when its files are links elsewhere"; then
    # Reached through a link from a sound directory, and nothing compose reads is a plain file in it,
    # so neither the path as written nor any file's path passes through the open directory; only the
    # deployment's resolved ancestors do.
    accounts y
    mkdir "$scratch/open" "$scratch/sound" "$scratch/opt"
    chmod 0777 "$scratch/open"
    chmod 0755 "$scratch/sound" "$scratch/opt"
    mv "$scratch/deploy/compose.yaml" "$scratch/sound/compose.yaml"
    rm "$scratch/deploy/.env"
    ln -s "$scratch/sound/compose.yaml" "$scratch/deploy/compose.yaml"
    mv "$scratch/deploy" "$scratch/open/deploy"
    ln -s "$scratch/open/deploy" "$scratch/opt/homespool"
    install_over "$scratch/opt/homespool"
    assert_refused "open ancestor, linked files"
    assert_contains "$out" "$scratch/open is writable by every account" "the open directory is named"
fi

if test_case "a compose file linked through a symlinked directory in a sound place is accepted"; then
    # The walk along the link as written passes through the directory link itself. On Linux that
    # link's own mode is 0777, which says nothing about the directory it names.
    accounts y
    mkdir "$scratch/real" "$scratch/sound"
    chmod 0755 "$scratch/real" "$scratch/sound"
    mv "$scratch/deploy/compose.yaml" "$scratch/real/compose.yaml"
    ln -s "$scratch/real" "$scratch/sound/via"
    ln -s "../sound/via/compose.yaml" "$scratch/deploy/compose.yaml"
    install_over
    assert_accepted "link through a sound directory link"
fi

if test_case "a compose file reached through two links is checked where it finally lands"; then
    # The first link names a sound directory; only the second leads into an open one.
    accounts y
    mkdir "$scratch/sound" "$scratch/open"
    chmod 0755 "$scratch/sound"
    chmod 0777 "$scratch/open"
    mv "$scratch/deploy/compose.yaml" "$scratch/open/compose.yaml"
    ln -s "$scratch/open/compose.yaml" "$scratch/sound/compose.yaml"
    ln -s "$scratch/sound/compose.yaml" "$scratch/deploy/compose.yaml"
    install_over
    assert_refused "two-hop link into an open directory"
    assert_contains "$out" "$scratch/open is writable by every account" "the final directory is named"
fi

if test_case "a compose file linked through a symlinked directory is checked along the link as written"; then
    # The resolved path is sound; the directory holding the directory link is not.
    accounts y
    mkdir "$scratch/real" "$scratch/open"
    chmod 0755 "$scratch/real"
    chmod 0777 "$scratch/open"
    mv "$scratch/deploy/compose.yaml" "$scratch/real/compose.yaml"
    ln -s "$scratch/real" "$scratch/open/via"
    ln -s "../open/via/compose.yaml" "$scratch/deploy/compose.yaml"
    install_over
    assert_refused "link through an open directory"
    assert_contains "$out" "$scratch/open is writable by every account" "the directory holding the link is named"
    assert_not_contains "$out" "/../" "and spelled without a .. in it"
fi

if test_case "a refusal names each path and reason once"; then
    accounts n
    install_over
    count="$(printf '%s\n' "$out" | grep -c "^  $scratch/deploy is owned by")"
    assert_equals "$count" 1 "the deployment directory is reported once"
fi

if test_case "a symlinked override pointing at nothing is refused"; then
    accounts y
    # Into a directory that exists: a missing directory fails any realpath, and only a missing last
    # component tells a check that insists on the target from one that does not.
    ln -s "$scratch/not-yet.yaml" "$scratch/deploy/compose.override.yaml"
    install_over
    assert_refused "dangling symlink"
    assert_contains "$out" "compose.override.yaml is a symlink to a file that does not exist" "and named"
fi

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed assertions passed."
else
    echo "$failed failed, $passed passed."
    exit 1
fi
