#!/usr/bin/env bash
#
# Tests for pi/files/homespool-wifi.sh.
#
#   tests/pi-wifi.test.sh              # run them all
#   tests/pi-wifi.test.sh psk          # run only tests whose name contains "psk"
#
# No test framework, for the reason tests/setup-env.test.sh gives at greater length: the harness is
# forty lines and needs nothing installed.
#
# Why this is worth testing, when the sibling suite's header says it is not: that header weighs the
# cost of a mistake and gets this script's wrong. A board that cannot join the network is exactly as
# unreachable as one nobody can log into - it is headless, it has no keyboard, and the remedies are
# a monitor and a spare HDMI cable or pulling the card. Half of what this script does is silent: an
# SSID hashed into the wrong file name, a carriage return that makes a good passphrase unusable, a
# passphrase left in the clear on a partition any desktop machine can read. None of them show up on
# a board where it worked.
#
# The script is driven through HOMESPOOL_WIFI_ROOT, which prefixes every path it touches, so a case
# is a directory tree plus stubs for the two commands that would otherwise talk to the running
# system. Nothing here unblocks a radio or restarts a supplicant.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/pi/files/homespool-wifi.sh"
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

assert_missing() {
    if [ -e "$1" ]; then
        fail "${2:-file should not exist}" "absent" "$1"
    else
        passed=$((passed + 1))
    fi
}

# A fresh tree per case: the boot partition's file, the directories iwd owns, and a bin directory
# holding stubs that record what they were called with instead of doing it.
#
# The stubs append rather than truncate, so "called twice" is visible.
#
# iwd is stopped by default, which is what a first boot looks like: this unit is ordered before the
# supplicant, so there is nothing to restart.
new_root() {
    root="$(mktemp -d "${TMPDIR:-/tmp}/pi-wifi.XXXXXX")"
    stub_dir="$root/stubs"

    mkdir -p "$root/boot/firmware" "$root/var/lib" "$root/etc" "$stub_dir"

    cat > "$stub_dir/rfkill" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$RECORD_DIR/rfkill.args"
STUB

    chmod +x "$stub_dir/rfkill"
    iwd_running no
}

# What `systemctl is-active --quiet iwd` answers. Everything else it is asked is recorded and
# accepted, which is how try-restart becomes visible.
iwd_running() {
    if [ "$1" = yes ]; then
        cat > "$stub_dir/systemctl" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$RECORD_DIR/systemctl.args"
exit 0
STUB
    else
        cat > "$stub_dir/systemctl" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$RECORD_DIR/systemctl.args"
case "$1" in
    is-active) exit 1 ;;
esac
exit 0
STUB
    fi

    chmod +x "$stub_dir/systemctl"
}

# The boot partition's file, written the way a user's editor would leave it - the comment block is
# not decoration here, since the script's blanking is a sed against a line in a file full of them,
# and the commented-out example is exactly what a careless pattern would match.
#
# %b so a case can embed \r.
write_conf() {
    {
        # One argument per line rather than one string of \n: the secret scanner's capture runs
        # across a literal \n, so the commented example below reaches it with the next line glued
        # on and stops reading as the placeholder it is.
        printf '%s\n' '# Homespool Wi-Fi setup' '#' '# ssid=MyNetwork' '# psk=hunter2' ''
        printf 'ssid=%b\n' "$1" # betterleaks:allow - a format string; the value is the case's own
        printf 'psk=%b\n' "$2"
        printf '\ncountry=%b\n' "${3:-}"
    } > "$root/boot/firmware/homespool-wifi.txt"
}

conf_line() {
    sed -n "s/^${1}=//p" "$root/boot/firmware/homespool-wifi.txt"
}

# iwd.network(5): the file is named for the SSID when it is a plain one, and "=" followed by its
# lower-case hex when it is not.
psk_file() {
    echo "$root/var/lib/iwd/$1.psk"
}

main_conf() {
    [ -f "$root/etc/iwd/main.conf" ] && cat "$root/etc/iwd/main.conf"
}

run_script() {
    RECORD_DIR="$root" HOMESPOOL_WIFI_ROOT="$root" PATH="$stub_dir:$PATH" \
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
# Applying a credential
# ------------------------------------------------------------------------------------------------

if test_case "a passphrase reaches iwd, with the modes it keeps a secret behind"; then
    write_conf "homenet" "swordfish"

    out="$(run_script)"

    assert_eq "[Security]
Passphrase=swordfish" "$(cat "$(psk_file homenet)")"
    assert_eq "600" "$(stat -c %a "$(psk_file homenet)")"
    assert_eq "700" "$(stat -c %a "$root/var/lib/iwd")"
    assert_contains "$out" "configured 'homenet'"
fi

if test_case "the psk line is blanked and the ssid left, so the card says what it joined"; then
    write_conf "homenet" "swordfish"

    run_script > /dev/null

    assert_eq "" "$(conf_line psk)" "FAT32 has no permissions; anyone with a card reader can read this"
    assert_eq "homenet" "$(conf_line ssid)"
fi

if test_case "a commented-out example is not what the blanking rewrites"; then
    write_conf "homenet" "swordfish"

    run_script > /dev/null

    assert_contains "$(cat "$root/boot/firmware/homespool-wifi.txt")" "# psk=hunter2" \
        "the sed is anchored on a real line, not on every line that mentions a psk"
fi

if test_case "an ssid that is not a plain name is hex-encoded the way iwd names files"; then
    # Getting this wrong does not error: iwd simply never matches the network to the credential,
    # which is a board that sees the network and will not join it.
    write_conf "my net!" "swordfish"

    run_script > /dev/null

    assert_eq "[Security]
Passphrase=swordfish" "$(cat "$(psk_file "=6d79206e657421")")"
fi

if test_case "an ssid of letters, digits, spaces, underscores and minus signs stays verbatim"; then
    write_conf "my_net-2 4G" "swordfish"

    run_script > /dev/null

    assert_eq "600" "$(stat -c %a "$(psk_file "my_net-2 4G")")" \
        "the four characters iwd permits in a file name are not the ones that trigger hex"
fi

if test_case "a carriage return a FAT32 editor added is not part of the passphrase"; then
    # Invisible in every editor, and iwd rejects the credential exactly as though the password were
    # wrong - the least debuggable outcome available on a board with no other way in.
    write_conf 'homenet\r' 'swordfish\r' 'DK\r'

    run_script > /dev/null

    assert_eq "[Security]
Passphrase=swordfish" "$(cat "$(psk_file homenet)")"
    assert_contains "$(main_conf)" "Country=DK"
fi

if test_case "a passphrase keeps its spaces, equals signs and hashes"; then
    write_conf "homenet" "two words=x#y"

    run_script > /dev/null

    assert_eq "[Security]
Passphrase=two words=x#y" "$(cat "$(psk_file homenet)")" \
        "everything after the first = is the value"
fi

# ------------------------------------------------------------------------------------------------
# iwd's own configuration, which must be written whole
# ------------------------------------------------------------------------------------------------

if test_case "the country reaches iwd and is reported"; then
    write_conf "homenet" "swordfish" "DK"

    out="$(run_script)"

    assert_contains "$(main_conf)" "Country=DK"
    assert_contains "$out" "regulatory country set to DK"
fi

if test_case "an empty country is not a failure"; then
    # The property, not the form: a board that joined its network on an optional line nobody filled
    # in must not be reported as a failed unit. Today nothing after the country block can carry its
    # status out, so this case would not catch the AND-list form on its own - it catches the block
    # ending up last, which is the only way that form bites.
    write_conf "homenet" "swordfish"

    out="$(run_script)"
    status=$?

    assert_eq 0 "$status" "nothing here failed, and a failed unit gets retried or reported"
    assert_eq "" "$(printf '%s' "$(main_conf)" | sed -n 's/^Country=//p')"
    assert_eq "" "$(printf '%s' "$out" | sed -n '/regulatory/p')"
fi

if test_case "the two lines that are not the country survive a write without one"; then
    # An earlier version wrote only the country here and silently discarded the rest with it.
    write_conf "homenet" "swordfish"

    run_script > /dev/null

    assert_contains "$(main_conf)" "EnableNetworkConfiguration=false"
    assert_contains "$(main_conf)" "SaeDisable=brcmfmac" \
        "without it iwd commits to SAE on this silicon and never associates"
fi

# ------------------------------------------------------------------------------------------------
# The quiet paths - this runs on every boot for the life of the card
# ------------------------------------------------------------------------------------------------

if test_case "an untouched file configures nothing and says nothing"; then
    write_conf "" ""

    out="$(run_script)"

    assert_missing "$root/var/lib/iwd" "no credential was offered, so iwd is left alone"
    assert_eq "" "$out"
fi

if test_case "a card whose boot file was deleted is not an error"; then
    rm -f "$root/boot/firmware/homespool-wifi.txt"

    out="$(run_script)"
    status=$?

    assert_eq 0 "$status"
    assert_eq "" "$out"
fi

if test_case "the radio is unblocked whether or not anything is configured"; then
    # rfkill_default.conf ships wireless blocked, and lifting it is unrelated to whether somebody
    # filled this file in - an ethernet deployment wants it lifted too.
    write_conf "" ""

    run_script > /dev/null

    assert_eq "unblock wifi" "$(recorded rfkill.args)"
fi

# ------------------------------------------------------------------------------------------------
# What becomes of the passphrase when it is not applied
# ------------------------------------------------------------------------------------------------

if test_case "a psk typed without an ssid is kept, and nothing is said about it"; then
    # Kept deliberately, the same trade the login suite makes for a failing chpasswd: a credential
    # the board could not use is the owner's to correct rather than this script's to discard, and
    # they have to open this file again anyway to supply the missing ssid. What it costs is a
    # passphrase sitting in the clear on the boot partition until they do.
    write_conf "" "swordfish"

    out="$(run_script)"

    assert_eq "swordfish" "$(conf_line psk)"
    assert_eq "" "$out"
fi

if test_case "a psk that could not be written is kept for another go"; then
    # The same trade the login suite makes for a failing chpasswd: a board nobody can reach must not
    # also lose the credential that would have reached it.
    : > "$root/var/lib/iwd"
    write_conf "homenet" "swordfish"

    run_script > /dev/null 2>&1

    assert_eq "swordfish" "$(conf_line psk)"
fi

# ------------------------------------------------------------------------------------------------
# The supplicant
# ------------------------------------------------------------------------------------------------

if test_case "iwd is left alone on a first boot, where it has not started yet"; then
    write_conf "homenet" "swordfish"

    run_script > /dev/null

    assert_eq "is-active --quiet iwd" "$(recorded systemctl.args)" \
        "this unit is ordered before iwd, so there is nothing to restart"
fi

if test_case "iwd is restarted when the file was edited on a running board"; then
    iwd_running yes
    write_conf "homenet" "swordfish"

    run_script > /dev/null

    assert_contains "$(recorded systemctl.args)" "try-restart iwd"
fi

# ------------------------------------------------------------------------------------------------

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed passed"
else
    echo "$passed passed, $failed FAILED"
fi

[ "$failed" -eq 0 ]
