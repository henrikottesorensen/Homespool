#!/usr/bin/env bash
#
# Tests for pi/files/homespool-login.sh.
#
#   tests/pi-login.test.sh              # run them all
#   tests/pi-login.test.sh gate         # run only tests whose name contains "gate"
#
# No test framework, for the reason tests/setup-env.test.sh gives at greater length: the harness is
# forty lines and needs nothing installed.
#
# What makes this worth testing at all, when its sibling homespool-wifi.sh is not: the card ships
# with the account locked and no authorised key, so this script is the only way onto the board - and
# half of what it does is refuse. A credential arriving once the account already has one, a carriage
# return a FAT32 editor added invisibly, a private key pasted where a public one goes, a build that
# never named the user. None of those show up on a board where it worked, and every one of them
# costs somebody an appliance with no shell on it.
#
# The independence of the two gates gets its own section, because it is the whole reason the file
# takes both lines and it is the part that a plausible simplification would quietly break.
#
# The script is driven through HOMESPOOL_LOGIN_ROOT, which prefixes every path it touches, so a
# case is a directory tree plus stubs for the commands that would otherwise read or change a real
# account. Nothing here runs chpasswd or passwd for real, and no key reaches the tester's own
# ~/.ssh - getent is stubbed, so the home directory is inside the sandbox.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/pi/files/homespool-login.sh"
filter="${1:-}"

passed=0
failed=0
current=""
root=""
stub_dir=""

# English, always - the assertions below quote the script's own prose.
export LC_ALL=C

# ------------------------------------------------------------------------------------------------
# Harness
# ------------------------------------------------------------------------------------------------

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $current"
    echo "        $1"
    [ $# -gt 1 ] && printf '        expected: %s\n        actual:   %s\n' "$2" "$3"
    return 0
}

assert_eq() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "${3:-values differ}" "$1" "$2"
    fi
}

assert_contains() {
    case "$1" in
        *"$2"*) passed=$((passed + 1)) ;;
        *) fail "${3:-substring not found}" "…$2…" "$1" ;;
    esac
}

# A fresh tree per case: the boot partition's file, the build-written defaults, and a bin directory
# holding stubs that record what they were called with instead of doing it.
#
# The stubs append rather than truncate, so "called twice" is visible - a script that set the
# password and then set it again would otherwise look identical to one that behaved.
#
# passwd -S is the gate the script reads. L is the state this image ships: the account exists and is
# locked, so there is no way in and no password to override.
new_root() {
    root="$(mktemp -d "${TMPDIR:-/tmp}/pi-login.XXXXXX")"
    stub_dir="$root/stubs"

    mkdir -p "$root/boot/firmware" "$root/etc/default" "$stub_dir"

    mkdir -p "$root/home/pi"

    printf 'HOMESPOOL_USER=pi\n' > "$root/etc/default/homespool-login"
    account_state L

    cat > "$stub_dir/chpasswd" <<'STUB'
#!/bin/sh
cat >> "$RECORD_DIR/chpasswd.stdin"
STUB

    # The home directory comes from the passwd database rather than an assumed /home/<user>, so the
    # sandbox has to answer for it. The script prefixes what comes back, which is what puts the key
    # inside the tree rather than in the tester's own ~/.ssh.
    cat > "$stub_dir/getent" <<'STUB'
#!/bin/sh
printf 'pi:x:1000:1000::/home/pi:/bin/bash\n'
STUB

    # chown wants a real user and root to run as, and neither is available here. What it would do is
    # covered by the modes asserted on the files themselves.
    cat > "$stub_dir/chown" <<'STUB'
#!/bin/sh
exit 0
STUB

    chmod +x "$stub_dir/chpasswd" "$stub_dir/getent" "$stub_dir/chown"
}

# A real ed25519 public key's shape - the prefix and the base64 are what the script checks and what
# sshd would parse, so a placeholder that merely looks keyish would test nothing.
KEY_A="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKj8Xk2mQq1vB7cR4tYuIoP0aSdFgHjKlZxCvBnM3qWe alice@laptop"
KEY_B="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL9pOiUyTrEwQaZxSvDcFgBhNjMkLoPqRsTuVwXyZ012 alice@desktop"

authorized_keys() {
    [ -f "$root/home/pi/.ssh/authorized_keys" ] && cat "$root/home/pi/.ssh/authorized_keys"
}

# What `passwd -S <user>` reports: P a usable password, NP an empty one, L a locked account. An
# empty argument stands for the account not being there at all, where passwd exits non-zero and
# prints nothing.
account_state() {
    if [ -n "$1" ]; then
        cat > "$stub_dir/passwd" <<STUB
#!/bin/sh
printf 'pi $1 09/03/2026 0 99999 7 -1\n'
STUB
    else
        cat > "$stub_dir/passwd" <<'STUB'
#!/bin/sh
exit 1
STUB
    fi

    chmod +x "$stub_dir/passwd"
}

# The boot partition's file, written the way a user's editor would leave it - the comment block is
# not decoration here, since the script's blanking is a sed against a line in a file full of them,
# and the commented-out examples are exactly what a careless pattern would match.
#
# $1 is the password line, $2 onwards are sshkey lines. %b so a case can embed \r.
write_conf() {
    {
        printf '# Homespool login\n#\n# password=hunter2\n# sshkey=ssh-ed25519 AAAA... you@host\n\n'
        printf 'password=%b\n' "$1"
        shift
        if [ $# -eq 0 ]; then
            printf 'sshkey=\n'
        else
            for key in "$@"; do
                printf 'sshkey=%b\n' "$key"
            done
        fi
    } > "$root/boot/firmware/homespool-login.txt"
}

conf_line() {
    sed -n "s/^${1:-password}=//p" "$root/boot/firmware/homespool-login.txt"
}

# Runs the script and prints what it said on both streams, which is where every refusal here lands.
run_script() {
    RECORD_DIR="$root" HOMESPOOL_LOGIN_ROOT="$root" PATH="$stub_dir:$PATH" \
        sh "$script" 2>&1
}

recorded() {
    [ -f "$root/$1" ] && cat "$root/$1"
}

test_case() {
    current="$1"
    case "$current" in
        *"$filter"*) ;;
        *) current=""; return 1 ;;
    esac
    echo "- $current"
    new_root
    return 0
}

# ------------------------------------------------------------------------------------------------
# Giving a locked account its first password
# ------------------------------------------------------------------------------------------------

if test_case "a locked account is given the password from the card, and the line blanked"; then
    write_conf "swordfish"

    out="$(run_script)"

    assert_eq "pi:swordfish" "$(recorded chpasswd.stdin)" "the account and the password it was given"
    assert_eq "" "$(conf_line)" "the password is not left in the clear on a FAT32 partition"
    assert_contains "$out" "password set for pi"
fi

if test_case "an empty password field counts as no way in, and is replaced"; then
    # NP is worse than locked, not better - anyone can log in with no password at all - so it is a
    # state to overwrite rather than one to protect.
    account_state NP
    write_conf "swordfish"

    run_script > /dev/null

    assert_eq "pi:swordfish" "$(recorded chpasswd.stdin)"
fi

if test_case "a carriage return a FAT32 editor added is not part of the password"; then
    # The single least debuggable outcome available: an account whose password is a string nobody
    # can type, on the one board that was supposed to still let you in.
    write_conf 'swordfish\r'

    run_script > /dev/null

    assert_eq "pi:swordfish" "$(recorded chpasswd.stdin)"
fi

if test_case "a password keeps its spaces, equals signs and colons"; then
    write_conf "two words=x:y"

    run_script > /dev/null

    assert_eq "pi:two words=x:y" "$(recorded chpasswd.stdin)" \
        "everything after the first = is the value, and chpasswd splits on the first colon"
fi

if test_case "an untouched file leaves the account locked and says nothing"; then
    write_conf ""

    out="$(run_script)"

    assert_eq "" "$(recorded chpasswd.stdin)" "no password was offered, so the account keeps none"
    assert_eq "" "$out" "every boot for the life of the card runs this; it must be quiet"
fi

if test_case "a card whose boot file was deleted is not an error"; then
    rm -f "$root/boot/firmware/homespool-login.txt"

    out="$(run_script)"

    assert_eq "" "$(recorded chpasswd.stdin)"
    assert_eq "" "$out"
fi

if test_case "the hatch is still open on a later boot, so a locked board can always be reached"; then
    # The recovery case, and the reason the gate is the account's state rather than the first boot:
    # a card flashed and booted with nothing filled in is not a board somebody has lost.
    write_conf ""
    run_script > /dev/null

    write_conf "swordfish"
    out="$(run_script)"

    assert_eq "pi:swordfish" "$(recorded chpasswd.stdin)"
    assert_contains "$out" "password set for pi"
fi

# ------------------------------------------------------------------------------------------------
# The gate: the first password, and only the first
# ------------------------------------------------------------------------------------------------

if test_case "an account that already has a password is not overridden by the gate file"; then
    account_state P
    write_conf "letmein"

    out="$(run_script)"

    assert_eq "" "$(recorded chpasswd.stdin)" "this file supplies the first password, never a later one"
    assert_contains "$out" "IGNORED"
    assert_contains "$out" "already has one"
    assert_contains "$out" "use"
    assert_contains "$out" "passwd"
    assert_eq "" "$(conf_line)" "and it is cleared rather than left sitting there in plaintext"
fi

if test_case "a blank file against an account with a password says nothing at all"; then
    account_state P
    write_conf ""

    out="$(run_script)"

    assert_eq "" "$out" "this is the steady state of every provisioned card; it must be silent"
fi

# ------------------------------------------------------------------------------------------------
# The SSH key
# ------------------------------------------------------------------------------------------------

if test_case "a key is installed into authorized_keys, with the modes sshd insists on"; then
    write_conf "" "$KEY_A"

    out="$(run_script)"

    assert_eq "$KEY_A" "$(authorized_keys)"
    assert_eq "700" "$(stat -c %a "$root/home/pi/.ssh")" "sshd ignores a group-writable .ssh outright"
    assert_eq "600" "$(stat -c %a "$root/home/pi/.ssh/authorized_keys")"
    assert_contains "$out" "authorised key installed for pi"
    assert_eq "" "$(conf_line sshkey)" "blanked so a provisioned card settles to an empty file"
fi

if test_case "a second sshkey line is somebody's other machine, not a mistake"; then
    write_conf "" "$KEY_A" "$KEY_B"

    run_script > /dev/null

    assert_eq "$KEY_A
$KEY_B" "$(authorized_keys)"
fi

if test_case "a carriage return does not corrupt the key's base64"; then
    # sshd rejects a line it cannot parse without saying anything, so this fails as "my key just
    # does not work" on a board whose only other way in may be a password nobody set.
    write_conf "" "$KEY_A\\r"

    run_script > /dev/null

    assert_eq "$KEY_A" "$(authorized_keys)"
fi

if test_case "something that is not a public key is refused, and said so, rather than installed"; then
    write_conf "" "AAAAC3NzaC1lZDI1NTE5AAAAIKj8Xk2mQq1vB7cR4tYuIoP0"

    out="$(run_script)"

    assert_eq "" "$(authorized_keys)" "an unparseable line would be skipped by sshd in silence"
    assert_contains "$out" "does not look like a public key"
    assert_contains "$out" ".pub file"
fi

if test_case "a private key pasted by mistake is refused before it reaches the disk"; then
    write_conf "" "-----BEGIN OPENSSH PRIVATE KEY-----"

    out="$(run_script)"

    assert_eq "" "$(authorized_keys)"
    assert_contains "$out" "does not look like a public key"
    assert_contains "$out" "-----BEGIN" "the refusal quotes what it was given, so it can be found"
fi

if test_case "a refused key is left in the file, because there is still something to fix"; then
    write_conf "" "not-a-key"

    run_script > /dev/null

    assert_eq "not-a-key" "$(conf_line sshkey)" "blanking it would take away the thing to correct"
fi

if test_case "an account that already has a key is not given another by the gate file"; then
    mkdir -p "$root/home/pi/.ssh"
    printf '%s\n' "$KEY_B" > "$root/home/pi/.ssh/authorized_keys"
    write_conf "" "$KEY_A"

    out="$(run_script)"

    assert_eq "$KEY_B" "$(authorized_keys)" "this file installs the first key, never a later one"
    assert_contains "$out" "IGNORED sshkey"
    assert_contains "$out" "already has an authorised key"
    assert_eq "" "$(conf_line sshkey)" "and it is cleared"
fi

# ------------------------------------------------------------------------------------------------
# The two gates are independent
#
# Which is the whole reason both lines exist: a key is no use at a monitor and keyboard, and a board
# whose networking has broken is exactly when the console is all there is.
# ------------------------------------------------------------------------------------------------

if test_case "both at once, on a card with neither"; then
    write_conf "swordfish" "$KEY_A"

    run_script > /dev/null

    assert_eq "pi:swordfish" "$(recorded chpasswd.stdin)"
    assert_eq "$KEY_A" "$(authorized_keys)"
fi

if test_case "a key-only card keeps its password line, which is the console recourse"; then
    account_state L
    mkdir -p "$root/home/pi/.ssh"
    printf '%s\n' "$KEY_B" > "$root/home/pi/.ssh/authorized_keys"
    write_conf "swordfish"

    out="$(run_script)"

    assert_eq "pi:swordfish" "$(recorded chpasswd.stdin)" \
        "an authorised key does nothing at the console, so it must not close the password gate"
    assert_contains "$out" "password set for pi"
fi

if test_case "a password-only card keeps its key line"; then
    account_state P
    write_conf "" "$KEY_A"

    run_script > /dev/null

    assert_eq "$KEY_A" "$(authorized_keys)" "having a password says nothing about wanting a key"
fi

if test_case "a refused password does not take the key down with it"; then
    # The case above leaves the password branch unentered, so it cannot see a gate that closed both.
    # This one offers a password *and* a key to an account that has the first and not the second:
    # the password is refused, and the key must still go in.
    account_state P
    write_conf "letmein" "$KEY_A"

    out="$(run_script)"

    assert_eq "" "$(recorded chpasswd.stdin)"
    assert_contains "$out" "IGNORED password"
    assert_eq "$KEY_A" "$(authorized_keys)" "one gate closing must not close the other"
    assert_contains "$out" "authorised key installed"
fi

if test_case "a card with both refuses both, and says which"; then
    account_state P
    mkdir -p "$root/home/pi/.ssh"
    printf '%s\n' "$KEY_B" > "$root/home/pi/.ssh/authorized_keys"
    write_conf "letmein" "$KEY_A"

    out="$(run_script)"

    assert_eq "" "$(recorded chpasswd.stdin)"
    assert_eq "$KEY_B" "$(authorized_keys)"
    assert_contains "$out" "IGNORED password"
    assert_contains "$out" "IGNORED sshkey"
    assert_eq "" "$(conf_line password)"
    assert_eq "" "$(conf_line sshkey)"
fi

# ------------------------------------------------------------------------------------------------
# The things that would silently do the wrong thing
# ------------------------------------------------------------------------------------------------

if test_case "a build that never named the device user leaves the password alone and says so"; then
    write_conf "swordfish"
    rm -f "$root/etc/default/homespool-login"

    out="$(run_script)"

    assert_eq "" "$(recorded chpasswd.stdin)" "guessing at the account is worse than doing nothing"
    assert_contains "$out" "no device user configured"
fi

if test_case "an account that cannot be read is left alone rather than assumed unlocked"; then
    # Failing closed: an unreadable state is not evidence of a locked account, and treating it as
    # one would set a password on something this script knows nothing about.
    account_state ""
    write_conf "swordfish"

    out="$(run_script)"
    status=$?

    assert_eq "" "$(recorded chpasswd.stdin)"
    assert_contains "$out" "cannot read the password state"
    assert_eq "swordfish" "$(conf_line)" "and the password is kept, since nothing was done with it"
    [ "$status" -ne 0 ] && passed=$((passed + 1)) || fail "the unit should be seen to have failed"
fi

if test_case "a failing chpasswd keeps the password on the card for another go"; then
    cat > "$stub_dir/chpasswd" <<'STUB'
#!/bin/sh
exit 1
STUB
    chmod +x "$stub_dir/chpasswd"
    write_conf "swordfish"

    run_script > /dev/null

    assert_eq "swordfish" "$(conf_line)" \
        "a board with no shell must not also lose the password that would have given it one"
fi

# ------------------------------------------------------------------------------------------------

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed passed"
else
    echo "$passed passed, $failed FAILED"
fi

[ "$failed" -eq 0 ]
