#!/usr/bin/env bash
#
# Tests for setup-env.sh.
#
#   tests/setup-env.test.sh              # run them all
#   tests/setup-env.test.sh route        # run only tests whose name contains "route"
#
# No test framework, deliberately. The script under test exists so an operator needs nothing
# installed before their first `docker compose up`, and a suite that needed bats on every developer's
# machine and in CI would undo half of that argument. The harness below is thirty lines.
#
# The interesting part is sandbox_path. Detection shells out to docker, ip, netstat and ifconfig, and
# which branch runs is decided by what exists on PATH - so a test builds a PATH holding only the
# stubs it wants plus the handful of real utilities the script needs. That is what lets the BSD route
# parser be exercised on Linux and the Linux one on a Mac, which no amount of running it by hand can
# do, and it is exactly where the hand-written parsing is most likely to be wrong.
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
filter="${1:-}"

passed=0
failed=0
current=""

# The PATH this was invoked with, kept only so docker can be found below.
real_path="$PATH"

# ------------------------------------------------------------------------------------------------
# A stock machine, not this one
#
# The whole suite runs on the operating system's own tools, with anything a developer has put in
# front of them stripped out. On a Mac here that means Homebrew's GNU sed, uutils coreutils and
# ugrep are all removed, leaving the BSD originals that a person running this script will actually
# have; on Linux /usr/bin is GNU and nothing changes.
#
# This is not tidiness. Every one of those replacements is a chance for the suite to pass on the
# maintainer's laptop and fail on a stock install, and awk is the standing example: macOS ships BWK
# awk, which rejects a bare ternary inside `print` that gawk accepts silently. That bug was in this
# script and reached a green run before the tests existed.
#
# Bash is the other half of it, and this file cannot fix that one - `tests/setup-env.test.sh` runs
# under whichever bash invoked it. Apple ships 3.2 and Homebrew ships 5.x, and 3.2 is what a stock
# Mac has, so run it BOTH ways:
#
#     /bin/bash tests/setup-env.test.sh        # what an operator's Mac actually has
#     tests/setup-env.test.sh                  # whatever is first on PATH
#
# The e2e cases below re-invoke the script with "$BASH" rather than letting the shebang pick, so the
# choice made here carries all the way through.
# ------------------------------------------------------------------------------------------------
system_path="/usr/bin:/bin:/usr/sbin:/sbin"
PATH="$system_path"

# Docker alone is added back, wherever it lives - a machine running this stack has it, and it is not
# part of what is being stripped. Symlinked into a directory of its own rather than by putting its
# whole parent on PATH, which on an Intel Mac would drag Homebrew back in through /usr/local/bin.
docker_bin="$(PATH="$real_path" command -v docker 2>/dev/null)" || docker_bin=""
if [ -n "$docker_bin" ]; then
    docker_dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-docker.XXXXXX")"
    ln -sf "$docker_bin" "$docker_dir/docker"
    PATH="$PATH:$docker_dir"
fi
real_path="$PATH"

# ------------------------------------------------------------------------------------------------
# Harness
# ------------------------------------------------------------------------------------------------

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $current"
    echo "        $1"
    [ $# -gt 1 ] && printf '        expected: %s\n        actual:   %s\n' "$2" "$3"
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

assert_succeeds() {
    if "$@"; then passed=$((passed + 1)); else fail "expected success: $*"; fi
}

assert_fails() {
    if "$@"; then fail "expected failure: $*"; else passed=$((passed + 1)); fi
}

test_case() {
    current="$1"
    case "$current" in
        *"$filter"*) ;;
        *) current=""; return 1 ;;
    esac
    echo "- $current"
    reset_state
    return 0
}

# The script keeps a few globals - the pending list, the docker cache - and a test that inherited
# the previous test's would pass or fail for reasons that have nothing to do with it.
reset_state() {
    pending=""
    dry_run=false
    non_interactive=false
    docker_subnets_cache=""
    docker_subnets_cached=false
    PATH="$real_path"
}

# A PATH holding only this case's stubs plus the real utilities the script actually calls. Anything
# not named here is genuinely absent, which is the point: `command -v ip` has to be able to fail.
sandbox_path() {
    local bin tool src stubs
    bin="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-bin.XXXXXX")"
    for tool in awk sed grep tr head cat seq mktemp chmod cp rm base64 stty sort uniq; do
        src="$(PATH="$system_path" command -v "$tool" 2>/dev/null)" \
            || src="$(PATH="$real_path" command -v "$tool" 2>/dev/null)" \
            || continue
        ln -sf "$src" "$bin/$tool"
    done
    # Several stub sets can be layered - "linux docker-collision" is a Linux host whose daemon
    # answers, which is a different case from either on its own.
    for stubs in "$@"; do
        [ -d "$tests_dir/stubs/$stubs" ] || continue
        cp "$tests_dir/stubs/$stubs"/* "$bin/"
    done
    chmod +x "$bin"/* 2>/dev/null || true
    PATH="$bin"
    sandbox_bins="$sandbox_bins $bin"
}

sandbox_bins=""

# A throwaway .env / .env.example pair, with the script pointed at it. Both are globals in the
# script, so a test can simply move them.
#
# Sets temp_env_dir rather than echoing it. Echoing invites `dir="$(use_temp_env ...)"`, and command
# substitution is a subshell - so the env_file assignment would be made and discarded, leaving the
# test silently operating on the PREVIOUS test's file. That cost an hour once; it does not echo now.
use_temp_env() {
    temp_env_dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-t.XXXXXX")"
    env_file="$temp_env_dir/.env"
    example_file="$temp_env_dir/.env.example"
    printf '%s\n' "$1" > "$example_file"
    if [ $# -gt 1 ] && [ -n "$2" ]; then
        printf '%s\n' "$2" > "$env_file"
    fi
}

# shellcheck source=../setup-env.sh
source "$repo_root/setup-env.sh"

# Sourcing brought the script's own `set -euo pipefail` into this shell, and -e is wrong for a test
# runner: half of what a suite does is call things expecting them to fail, and the first one would
# end the run silently on the assertion before the summary. The script keeps -e when it is executed,
# which is the case that matters.
set +e

# ------------------------------------------------------------------------------------------------
# The IPv4 arithmetic
#
# Hand-rolled, so it is the part of this script most worth pinning down.
# ------------------------------------------------------------------------------------------------

if test_case "ip_to_int converts and rejects"; then
    assert_eq "0" "$(ip_to_int 0.0.0.0)" "all zeroes"
    assert_eq "16909060" "$(ip_to_int 1.2.3.4)" "1.2.3.4"
    assert_eq "4294967295" "$(ip_to_int 255.255.255.255)" "broadcast, and no sign overflow"
    assert_eq "3232239086" "$(ip_to_int 192.168.13.238)" "a real LAN address"
    assert_fails ip_to_int "not-an-address"
    assert_fails ip_to_int "192.168.1"
    assert_fails ip_to_int ""
fi

if test_case "cidr_overlap catches containment in both directions"; then
    assert_succeeds cidr_overlap 172.28.0.0/16 172.28.0.0/16
    assert_succeeds cidr_overlap 172.28.0.0/16 172.28.5.0/24
    assert_succeeds cidr_overlap 172.28.5.0/24 172.28.0.0/16
    assert_fails cidr_overlap 172.28.0.0/16 192.168.13.0/24
    assert_fails cidr_overlap 172.28.0.0/16 172.29.0.0/16
    # A /8 contains a /16 that a naive equal-prefix comparison would miss.
    assert_succeeds cidr_overlap 172.0.0.0/8 172.28.0.0/16
    # 0.0.0.0/0 contains everything, and the 32-bit shift it implies must not misbehave.
    assert_succeeds cidr_overlap 0.0.0.0/0 192.168.13.0/24
    # A malformed range is a failure, not a match.
    assert_fails cidr_overlap "garbage/16" 172.28.0.0/16
fi

if test_case "ip_in_cidr places an address"; then
    assert_succeeds ip_in_cidr 172.28.0.2 172.28.0.0/16
    assert_fails ip_in_cidr 192.168.13.238 172.28.0.0/16
    assert_succeeds ip_in_cidr 192.168.13.238 192.168.13.0/24
fi

if test_case "overlaps_any reports which ranges it hit"; then
    hits="$(overlaps_any 172.28.0.0/16 <<'EOF'
172.17.0.0/16

172.28.0.0/16
192.168.13.0/24
172.28.9.0/24
EOF
)"
    assert_eq "172.28.0.0/16
172.28.9.0/24" "$hits" "both overlapping ranges, blank line skipped"
    assert_fails overlaps_any 10.0.0.0/8 <<< "172.17.0.0/16"
fi

# ------------------------------------------------------------------------------------------------
# Route parsing, each dialect run on whatever platform this happens to be
# ------------------------------------------------------------------------------------------------

if test_case "host_routes parses BSD netstat abbreviations"; then
    sandbox_path bsd
    routes="$(host_routes)"
    # netstat prints "192.168.13" for a /24; without expansion this reads as a /32 and a collision
    # with the whole LAN goes unnoticed.
    assert_contains "$routes" "192.168.13.0/24" "abbreviated destination expanded"
    assert_contains "$routes" "127.0.0.0/8" "a single octet is a /8"
    assert_contains "$routes" "224.0.0.0/4" "an explicit prefix is kept, octets padded"
    assert_contains "$routes" "192.168.13.1/32" "a /32 host route survives"
    case "$routes" in
        *default*) fail "the default route leaked in as a CIDR" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

if test_case "host_routes parses Linux ip route"; then
    sandbox_path linux
    routes="$(host_routes)"
    assert_contains "$routes" "192.168.13.0/24" "a plain CIDR"
    assert_contains "$routes" "172.17.0.0/16" "docker0's range"
    case "$routes" in
        *default*) fail "the default route leaked in as a CIDR" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

# ------------------------------------------------------------------------------------------------
# Docker, and the addresses it makes unusable
# ------------------------------------------------------------------------------------------------

if test_case "docker_subnets excludes this stack's own network"; then
    sandbox_path docker-collision
    subnets="$(docker_subnets)"
    assert_contains "$subnets" "172.17.0.0/16" "another stack's network is listed"
    case "$subnets" in
        # The stub labels 172.28.0.0/16 as ours; offering it back would make the wizard propose
        # moving off a range only it is using.
        *172.28.0.0/16*) fail "our own compose network was not excluded" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

if test_case "lan_addresses falls back to the whole pool when Docker cannot be asked"; then
    # The linux sandbox has no docker stub, so the daemon query yields nothing. Before this was
    # handled, the filter silently passed everything and 172.17.0.1 was offered as reachable - the
    # one address guaranteed to be wrong, and the one that gets frozen into a certificate.
    sandbox_path linux
    addresses="$(lan_addresses 2>/dev/null)"
    assert_contains "$addresses" "192.168.13.238" "the real LAN address survives the conservative filter"
    case "$addresses" in
        *172.17.0.1*) fail "a Docker address was offered with no daemon to rule it out" ;;
        *) passed=$((passed + 1)) ;;
    esac
    assert_contains "$(lan_addresses 2>&1 >/dev/null)" "Could not ask Docker" "and it says so"
fi

if test_case "lan_addresses drops what a printer cannot reach"; then
    # A Linux host whose daemon does answer, so the precise filter applies rather than the fallback.
    sandbox_path linux docker-collision
    addresses="$(lan_addresses)"
    assert_contains "$addresses" "192.168.13.238" "the real LAN address is offered"
    for unusable in 127.0.0.1 169.254.1.1 172.17.0.1; do
        case "$addresses" in
            *"$unusable"*) fail "$unusable was offered as reachable" ;;
            *) passed=$((passed + 1)) ;;
        esac
    done
fi

if test_case "free_subnet skips what is taken"; then
    sandbox_path docker-collision
    # The stub holds 172.16-172.19 and 172.28; the first free /16 is 172.20.
    assert_eq "172.20.0.0/16" "$(free_subnet)" "first genuinely free range"
fi

if test_case "the no-collision path survives set -e"; then
    # The script runs under `set -e`, and "no collision" is reported by FAILING - so an assignment
    # from that substitution ends the run. It did: the interactive flow died silently after the
    # camera credential, having written nothing, and every function-level test still passed because
    # they run with -e off and the --set path never reaches this code.
    #
    # Run in a subshell with -e on, the way the real script has it.
    sandbox_path linux docker-collision
    use_temp_env "PROXY_SUBNET=172.28.0.0/16
PROXY_NETWORK=172.28.0.0/16"
    ( set -e; check_subnet_collision >/dev/null 2>&1; echo reached-the-end ) > "$temp_env_dir/out"
    assert_eq "reached-the-end" "$(cat "$temp_env_dir/out")" "returned rather than exiting"
    assert_eq "" "$pending" "and planned nothing, because 172.28 is free here"
fi

if test_case "a real collision is detected and a free range proposed"; then
    sandbox_path linux docker-collision
    # The stub's own network is 172.17-172.19 plus a labelled 172.28. Ask about 172.17, which is
    # genuinely taken by another stack.
    use_temp_env "PROXY_SUBNET=172.17.0.0/16
PROXY_NETWORK=172.17.0.0/16"
    out="$(check_subnet_collision 2>&1 <<< "n")"
    assert_contains "$out" "collides with" "said so"
    assert_contains "$out" "172.20.0.0/16" "proposed the first free range"
fi

if test_case "detect_timezone reads the zoneinfo symlink"; then
    sandbox_path tz
    assert_eq "Europe/Copenhagen" "$(detect_timezone)" "from readlink /etc/localtime"
fi

# ------------------------------------------------------------------------------------------------
# The dollar that eats passwords
#
# SMTP_PASSWORD=p@ss$word reaches the container as "p@ss". Regression test for a live defect.
# ------------------------------------------------------------------------------------------------

if test_case "compose escaping round-trips a dollar"; then
    assert_eq 'p@ss$$word' "$(compose_escape 'p@ss$word')" "escaped on the way in"
    assert_eq 'p@ss$word' "$(compose_unescape 'p@ss$$word')" "unescaped on the way out"
    for secret in 'p@ss$word' 'a$b$c' 'no-dollar-here' '$' 'trailing$'; do
        assert_eq "$secret" "$(compose_unescape "$(compose_escape "$secret")")" "round trip: $secret"
    done
fi

# ------------------------------------------------------------------------------------------------
# Reading, planning and writing
# ------------------------------------------------------------------------------------------------

if test_case "env_get prefers .env, falls back to .env.example"; then
    use_temp_env "PRINTER_HOST=
USER_HOST=localhost
TZ=UTC" "PRINTER_HOST=set.lan" > /dev/null
    assert_eq "set.lan" "$(env_get PRINTER_HOST)" "from .env"
    assert_eq "localhost" "$(env_get USER_HOST)" "fell through to .env.example"
fi

if test_case "env_get treats an emptied key as chosen, not absent"; then
    # SMTP_HOST= means "no outgoing mail" and is a supported configuration. Falling back to the
    # example's default here would re-offer a mail server to somebody who deliberately cleared it.
    use_temp_env "SMTP_HOST=mail.example.com" "SMTP_HOST=" > /dev/null
    assert_eq "" "$(env_get SMTP_HOST)" "the empty value in .env wins"
fi

if test_case "plan_set drops a no-op but keeps a default made explicit"; then
    use_temp_env "PRINTER_HOST=
TZ=UTC" "PRINTER_HOST=set.lan" > /dev/null
    plan_set PRINTER_HOST set.lan
    assert_eq "" "$pending" "same value as the file already holds is not a change"

    plan_set PRINTER_HOST other.lan
    assert_eq "PRINTER_HOST=other.lan" "$(echo "$pending" | head -1)" "a real change is planned"

    # TZ is absent from .env, so writing UTC is a change even though it equals the example default.
    # Dropping it would silently do nothing while reporting success.
    pending=""
    plan_set TZ UTC
    assert_eq "TZ=UTC" "$(echo "$pending" | head -1)" "an absent key is written even at its default"
fi

if test_case "apply leaves everything it was not asked about"; then
    use_temp_env "PRINTER_HOST=
TZ=UTC" "# My own notes at the top, do not delete.
PRINTER_TLS=false          # hand-edited: I read the wire in the clear
PRINTER_HOST=old.lan

# A key the wizard has never heard of
MY_CUSTOM_THING=keepme"
    plan_set PRINTER_HOST 192.168.13.238
    apply >/dev/null 2>&1

    assert_eq "# My own notes at the top, do not delete.
PRINTER_TLS=false          # hand-edited: I read the wire in the clear
PRINTER_HOST=192.168.13.238

# A key the wizard has never heard of
MY_CUSTOM_THING=keepme" "$(cat "$env_file")" "one line changed, every other byte identical"
fi

if test_case "apply appends a key the file never mentioned"; then
    use_temp_env "TZ=UTC" "PRINTER_HOST=old.lan" > /dev/null
    plan_set TZ Europe/Copenhagen
    apply >/dev/null 2>&1
    assert_eq "PRINTER_HOST=old.lan
TZ=Europe/Copenhagen" "$(cat "$env_file")" "appended, nothing rewritten"
fi

if test_case "apply rewrites the last assignment, which is the one read"; then
    # A shell and compose both take the last, and so does file_get. Rewriting the first would show
    # one value in the summary and change a different line.
    use_temp_env "PRINTER_HOST=" "PRINTER_HOST=first.lan
USER_HOST=x
PRINTER_HOST=second.lan" > /dev/null
    assert_eq "second.lan" "$(env_get PRINTER_HOST)" "reading takes the last"
    plan_set PRINTER_HOST third.lan
    apply >/dev/null 2>&1
    assert_eq "PRINTER_HOST=first.lan
USER_HOST=x
PRINTER_HOST=third.lan" "$(cat "$env_file")" "writing takes the last too"
fi

if test_case "apply writes a password exactly as typed"; then
    use_temp_env "SMTP_PASSWORD=" "SMTP_PASSWORD=old" > /dev/null
    plan_set SMTP_PASSWORD 'p@ss\w/o&rd"$x'
    apply >/dev/null 2>&1
    # Stored escaped, because a bare $ would be eaten by compose...
    assert_eq 'SMTP_PASSWORD=p@ss\w/o&rd"$$x' "$(cat "$env_file")" "backslash, slash and ampersand survive awk"
    # ...and read back as what was typed.
    assert_eq 'p@ss\w/o&rd"$x' "$(env_get SMTP_PASSWORD)" "round trips through the file"
fi

if test_case "apply seeds from .env.example when there is no .env"; then
    use_temp_env "# Documentation nobody should lose.
PRINTER_HOST=
TZ=UTC" > /dev/null
    plan_set PRINTER_HOST 192.168.13.238
    apply >/dev/null 2>&1
    assert_contains "$(cat "$env_file")" "# Documentation nobody should lose." "the comments came with it"
    assert_eq "192.168.13.238" "$(env_get PRINTER_HOST)" "and the answer was applied"
fi

if test_case "apply tightens the mode on a file holding a password"; then
    use_temp_env "SMTP_PASSWORD=" > /dev/null
    plan_set SMTP_PASSWORD hunter2
    apply >/dev/null 2>&1
    # GNU stat first: this repo's developer machine has GNU coreutils ahead of BSD on PATH, so
    # `stat -f` there means "describe the filesystem" and prints a block report rather than a mode.
    mode="$(stat -c '%a' "$env_file" 2>/dev/null || stat -f '%Lp' "$env_file")"
    assert_eq "600" "$mode" "not world-readable"
fi

# ------------------------------------------------------------------------------------------------
# Prompts
#
# Reachable because the tty guard lives in main(), which sourcing does not run.
# ------------------------------------------------------------------------------------------------

if test_case "ask takes the default on an empty answer"; then
    assert_eq "192.168.13.238" "$(ask "Which" "192.168.13.238" 2>/dev/null <<< "")" "empty means default"
    assert_eq "typed.lan" "$(ask "Which" "192.168.13.238" 2>/dev/null <<< "typed.lan")" "an answer wins"
fi

if test_case "ask_yes_no re-asks rather than guessing"; then
    assert_succeeds ask_yes_no "Go" y 2>/dev/null <<< "y"
    assert_fails ask_yes_no "Go" y 2>/dev/null <<< "n"
    assert_succeeds ask_yes_no "Go" y 2>/dev/null <<< ""
    # Nonsense is refused and the question repeated, rather than read as either answer.
    assert_succeeds ask_yes_no "Go" y 2>/dev/null <<< "maybe
y"
fi

# ------------------------------------------------------------------------------------------------
# End to end
# ------------------------------------------------------------------------------------------------

if test_case "--set is idempotent"; then
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$dir/"
    cp "$repo_root/setup-env.sh" "$dir/"

    "$BASH" "$dir/setup-env.sh" --set PRINTER_HOST=192.168.13.238 >/dev/null 2>&1
    first="$(cat "$dir/.env")"

    out="$("$BASH" "$dir/setup-env.sh" --set PRINTER_HOST=192.168.13.238 2>&1)"
    assert_contains "$out" "Nothing to change" "a second identical run plans nothing"
    assert_eq "$first" "$(cat "$dir/.env")" "and writes nothing"
fi

if test_case "--dry-run writes nothing"; then
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$dir/"
    cp "$repo_root/setup-env.sh" "$dir/"
    "$BASH" "$dir/setup-env.sh" --set PRINTER_HOST=192.168.13.238 >/dev/null 2>&1
    before="$(cat "$dir/.env")"
    "$BASH" "$dir/setup-env.sh" --dry-run --set PRINTER_HOST=changed.lan >/dev/null 2>&1
    assert_eq "$before" "$(cat "$dir/.env")" "unchanged"
fi

if test_case "interactive with no tty refuses instead of hanging"; then
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$dir/"
    cp "$repo_root/setup-env.sh" "$dir/"
    out="$("$BASH" "$dir/setup-env.sh" < /dev/null 2>&1)"
    status=$?
    assert_contains "$out" "nothing to read answers from" "says why"
    assert_eq "1" "$status" "and fails"
fi

# ------------------------------------------------------------------------------------------------

for b in $sandbox_bins; do rm -rf "$b"; done

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed assertions passed."
else
    echo "$failed failed, $passed passed."
    exit 1
fi
