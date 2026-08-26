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

# English, always. Every prose assertion below matches the strings as written in the script, and the
# script is translatable now - so on a machine whose locale has a catalogue the suite would compare
# Danish output against English expectations and go red for no reason. C also switches bash's $"..."
# off entirely, so this pins the behaviour rather than merely the language.
export LC_ALL=C
export LANGUAGE=
unset TEXTDOMAINDIR 2>/dev/null || true

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

# For prose the script prints. The wording is wrapped to fit 80 columns, so any assertion matching
# more than a couple of words can span a line break - and then re-flowing a paragraph breaks a test
# that has nothing to do with the change. Both sides are collapsed to single spaces, so these assert
# on what was said rather than on where it happened to wrap.
assert_says() {
    local haystack needle
    # Trimmed as well as collapsed: echo adds a newline, which tr turns into a trailing space, and a
    # needle ending in a space matches nothing.
    haystack="$(echo "$1" | tr '\n' ' ' | tr -s ' ' | sed 's/^ *//; s/ *$//')"
    needle="$(echo "$2" | tr '\n' ' ' | tr -s ' ' | sed 's/^ *//; s/ *$//')"
    case "$haystack" in
        *"$needle"*) passed=$((passed + 1)) ;;
        # Braced: bash reads the bytes of a multibyte character as part of the name otherwise, and
        # under `set -u` "…$needle…" dies with "needle<mojibake>: unbound variable".
        *) fail "${3:-not said}" "…${needle}…" "$1" ;;
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
    # Re-sourced, because a test that overrides a function and then unsets it does not restore the
    # original - it deletes it. The WSL test did exactly that to is_wsl, and every later test in the
    # file then ran without it, reporting "command not found" into results that still looked green.
    # shellcheck source=../setup-env.sh
    source "$repo_root/setup-env.sh"
    set +e

    pending=""
    dry_run=false
    non_interactive=false
    no_prompt=false
    no_overwrite=false
    unset HOMESPOOL_HOSTNAME HOMESPOOL_WINDOWS_TZ HOMESPOOL_WINDOWS_REGION HOMESPOOL_HOST_DLL
    # A VAR=value prefix only scopes to a COMMAND. `HOMESPOOL_ADDRESSES=x out="$(f)"` is an
    # assignment, not a command, so the prefix is an ordinary assignment too and stays set for the
    # rest of the run - which is how a later test came to be handed a vEthernet address by an
    # earlier one. Cleared here rather than trusted to be scoped.
    unset HOMESPOOL_ADDRESSES
    # The "cannot ask Docker" explanation is printed once per run, and the marker that enforces that
    # is a file - so it has to be cleared between tests or the second test never sees it.
    rm -f "${docker_warning_marker:-}" 2>/dev/null || true
    docker_subnets_cache=""
    docker_subnets_cached=false
    PATH="$real_path"
}

# A PATH holding only this case's stubs plus the real utilities the script actually calls. Anything
# not named here is genuinely absent, which is the point: `command -v ip` has to be able to fail.
sandbox_path() {
    local bin tool src stubs stub
    bin="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-bin.XXXXXX")"
    # Everything the script shells out to. A missing one is not a soft failure: the script derives
    # its own directory with dirname on line one, so an absent dirname breaks it before it starts.
    # This list has now bitten three times - date, tail, fold - always the same way: the missing tool
    # surfaces as an unrelated assertion failing, never as "this tool is missing". Writing the rule
    # down did not stop the third one, so there is a test below that fails on "command not found"
    # anywhere in a full run, which names the cause instead of leaving it to be deduced.
    #
    # date is on this list for a reason worth keeping: it was not, the backup step added later used
    # it in a command substitution, and under `set -e` that failure ended the script between seeding
    # .env and patching it. The suite went red for a fault the code did not have, while a real
    # fragility - a nicety able to abort the write - hid behind it. A tool missing here does not
    # report itself; it changes behaviour somewhere else.
    for tool in awk sed grep tr head cat seq mktemp chmod cp rm base64 stty sort uniq \
                dirname basename ln mkdir openssl getent hostname date tail timeout fold tput; do
        src="$(PATH="$system_path" command -v "$tool" 2>/dev/null)" \
            || src="$(PATH="$real_path" command -v "$tool" 2>/dev/null)" \
            || continue
        ln -sf "$src" "$bin/$tool"
    done
    # Several stub sets can be layered - "linux docker-collision" is a Linux host whose daemon
    # answers, which is a different case from either on its own.
    #
    # The destination is REMOVED before each copy, and that is not tidiness: the entries above are
    # symlinks to real binaries, and `cp` over a symlink follows it and writes to the target. A stub
    # sharing a name with a tool in that list - hostname does - would otherwise overwrite the actual
    # binary in /usr/bin.
    for stubs in "$@"; do
        [ -d "$tests_dir/stubs/$stubs" ] || continue
        for stub in "$tests_dir/stubs/$stubs"/*; do
            [ -f "$stub" ] || continue
            rm -f "$bin/$(basename "$stub")"
            cp "$stub" "$bin/"
        done
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

if test_case "the two subnet questions get two different answers"; then
    # They were one list, and it was right for one question and wrong for the other. On the Pi that
    # showed up as 172.28.0.1 on br-2deab6694565 - the host's end of OUR OWN compose bridge - being
    # offered as somewhere a printer could reach, because the range had been excluded for the
    # collision check's benefit.
    sandbox_path docker-collision

    # Reachability: everything counts, ours included. An address in our bridge is as unreachable to
    # a printer as one in anybody else's.
    assert_contains "$(docker_subnets)" "172.28.0.0/16" "our own network is in the unreachable list"
    assert_contains "$(docker_subnets)" "172.17.0.0/16" "and so is another stack's"

    # Collision: ours does not count, because a stack does not collide with itself.
    case "$(docker_subnets_excluding_ours)" in
        *172.28.0.0/16*) fail "our own network counted as a collision with itself" ;;
        *) passed=$((passed + 1)) ;;
    esac
    assert_contains "$(docker_subnets_excluding_ours)" "172.17.0.0/16" "another stack's still does"
fi

if test_case "an address in our own compose bridge is not offered"; then
    # The Pi's actual symptom, as its own case.
    sandbox_path linux docker-collision
    HOMESPOOL_ADDRESSES="192.168.13.183	wlan0
172.28.0.1	br-2deab6694565"
    export HOMESPOOL_ADDRESSES
    addresses="$(lan_addresses)"
    unset HOMESPOOL_ADDRESSES

    assert_contains "$addresses" "192.168.13.183" "the real address is offered"
    case "$addresses" in
        *172.28.0.1*) fail "the host end of our own compose bridge was offered" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

if test_case "lan_addresses falls back to the whole pool when Docker cannot be asked"; then
    # The linux sandbox has no docker stub, so the daemon query yields nothing. Before this was
    # handled, the filter silently passed everything and 172.17.0.1 was offered as reachable - the
    # one address guaranteed to be wrong, and the one that gets frozen into a certificate.
    sandbox_path linux
    # Pinned, because the suite itself runs inside a container on Linux - so the real in_container
    # would be true there and false on a Mac, and this case is about the HOST wording.
    in_container() { return 1; }
    addresses="$(lan_addresses 2>/dev/null)"
    assert_contains "$addresses" "192.168.13.238" "the real LAN address survives the conservative filter"
    case "$addresses" in
        *172.17.0.1*) fail "a Docker address was offered with no daemon to rule it out" ;;
        *) passed=$((passed + 1)) ;;
    esac
    # Cleared first: the explanation is deliberately printed once per run.
    rm -f "$docker_warning_marker"
    assert_says "$(lan_addresses 2>&1 >/dev/null)" "Could not ask Docker" "and it says so"
    unset -f in_container
fi

if test_case "the container explanation is printed once, not once per lookup"; then
    # It appeared three times in one run on Windows. The flag that was meant to stop that was a
    # variable, set inside a command substitution - so it lived in a subshell and died with it,
    # every time.
    sandbox_path linux
    in_container() { return 0; }
    rm -f "$docker_warning_marker"

    first="$(lan_addresses 2>&1 >/dev/null)"
    second="$(lan_addresses 2>&1 >/dev/null)"
    unset -f in_container

    assert_says "$first" "Running inside a container" "said once"
    assert_eq "" "$second" "and not again"
fi

if test_case "inside a container it does not blame the daemon"; then
    # The Windows path runs this INSIDE a container, which has no docker CLI and no socket - so the
    # query cannot succeed and "is it installed and running?" is nonsense, since Docker is running
    # the very container asking.
    sandbox_path linux
    in_container() { return 0; }
    rm -f "$docker_warning_marker"
    out="$(lan_addresses 2>&1 >/dev/null)"
    unset -f in_container

    assert_says "$out" "Running inside a container" "says what is actually true"
    case "$out" in
        *"is it installed and running"*) fail "still blaming the daemon from inside it" ;;
        *) passed=$((passed + 1)) ;;
    esac
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

if test_case "candidates carry the interface that owns them"; then
    # The filtering can only remove what it can prove is unreachable. Two real NICs both up on the
    # same subnet - Ethernet and Wi-Fi - are both genuinely reachable and identical as addresses, so
    # nothing but the interface name lets anyone choose.
    sandbox_path linux docker-collision
    addresses="$(lan_addresses)"

    assert_contains "$addresses" "192.168.13.238	eth0" "the wired address, named"
    assert_contains "$addresses" "192.168.13.51	wlan0" "and the wireless one"
    case "$addresses" in
        *172.17.0.1*) fail "docker0 was offered" ;;
        *) passed=$((passed + 1)) ;;
    esac
    case "$addresses" in
        *127.0.0.1*) fail "loopback was offered" ;;
        *) passed=$((passed + 1)) ;;
    esac
    # The preferred candidate leads, and arrives labelled from `route get` rather than unnamed.
    assert_eq "192.168.13.238	eth0" "$(echo "$addresses" | head -1)" "the address traffic uses, first"
fi

if test_case "an address named twice is offered once"; then
    # `route get` names it and `-o addr show` names it again; dedupe is on the address, not the line.
    sandbox_path linux docker-collision
    assert_eq "1" "$(lan_addresses | grep -c '^192\.168\.13\.238')" "not repeated"
fi

if test_case "auto_answer takes the address, not the label"; then
    sandbox_path linux docker-collision
    use_temp_env "PRINTER_HOST=
USER_HOSTS=localhost
TZ=UTC
GO2RTC_USERNAME=
GO2RTC_PASSWORD="
    auto_answer >/dev/null 2>&1
    assert_contains "$pending" "PRINTER_HOST=192.168.13.238
" "no tab or interface name written into .env"
fi

if test_case "under WSL it asks Windows rather than the VM"; then
    # `ip route get` inside WSL answers about the NAT'd VM, so the address it returns is useless to
    # a printer - the same failure as inside a container. WSL can execute Windows binaries, so the
    # script asks Windows directly and needs no wrapper. This is the only route on a machine where
    # Docker Desktop will not install.
    sandbox_path linux wsl
    marker="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-wsl.XXXXXX")"
    is_wsl() { return 0; }          # the interop file cannot be faked on a Mac
    addresses="$(lan_addresses 2>/dev/null)"
    unset -f is_wsl

    assert_contains "$addresses" "192.168.13.50" "took the Windows LAN address"
    for unusable in 172.28.112.1 172.17.240.1 127.0.0.1; do
        case "$addresses" in
            *"$unusable"*) fail "$unusable was offered - vEthernet or loopback" ;;
            *) passed=$((passed + 1)) ;;
        esac
    done
    # Windows line endings would otherwise ride along and make every address unparseable.
    case "$addresses" in
        *$'\r'*) fail "carriage returns survived into the address list" ;;
        *) passed=$((passed + 1)) ;;
    esac
    rm -rf "$marker"
fi

if test_case "HOMESPOOL_ADDRESSES supplies what a sandbox cannot see"; then
    # The Windows path: neither a container nor WSL2 can see the Windows host's LAN address, so
    # setup-env.ps1 asks Windows and passes the list in. It is a fact, not an answer - everything
    # else still applies to it.
    sandbox_path linux docker-collision
    HOMESPOOL_ADDRESSES="192.168.13.50 172.17.0.1 127.0.0.1 169.254.9.9" \
        addresses="$(lan_addresses)"
    assert_eq "192.168.13.50" "$addresses" "the supplied LAN address, with the rest filtered out"

    # Comma-separated too, because that is what a PowerShell array interpolates to.
    HOMESPOOL_ADDRESSES="192.168.13.50,192.168.13.51" addresses="$(lan_addresses)"
    assert_eq "192.168.13.50
192.168.13.51" "$addresses" "both, in the order given"
fi

if test_case "HOMESPOOL_ADDRESSES carries interface names too"; then
    # setup-env.ps1 sends one entry per line, address then tab then interface. It cannot use the
    # space-separated form, because "vEthernet (WSL)" contains a space and would be split into two
    # bogus entries - which is precisely the name that most needs to be readable.
    sandbox_path linux docker-collision
    HOMESPOOL_ADDRESSES="192.168.13.50	Ethernet
192.168.13.51	Wi-Fi
127.0.0.1	Loopback Pseudo-Interface 1"
    export HOMESPOOL_ADDRESSES
    addresses="$(lan_addresses)"
    unset HOMESPOOL_ADDRESSES

    assert_contains "$addresses" "192.168.13.50	Ethernet" "the name survived the space in it"
    assert_contains "$addresses" "192.168.13.51	Wi-Fi" "and the second entry"
    case "$addresses" in
        *Loopback*) fail "loopback came through with its label" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

if test_case "HOMESPOOL_ADDRESSES still loses a vEthernet address with no daemon"; then
    # On Windows the supplied list contains WSL and Hyper-V vEthernet addresses in 172.x, and the
    # container has no Docker socket - so the conservative 172.16/12 fallback is what removes them.
    # Being unable to ask Docker is the normal case there, not a degradation.
    sandbox_path linux
    HOMESPOOL_ADDRESSES="172.28.112.1 192.168.13.50" addresses="$(lan_addresses 2>/dev/null)"
    assert_eq "192.168.13.50" "$addresses" "the vEthernet address is not offered"
fi

if test_case "free_subnet skips what is taken"; then
    sandbox_path docker-collision
    # The stub holds 172.16-172.19 and 172.28; the first free /16 is 172.20.
    assert_eq "172.20.0.0/16" "$(free_subnet)" "first genuinely free range"
fi

if test_case "a running stack does not collide with itself"; then
    # The Pi said "172.28.0.0/16 collides with: 172.28.0.0/16" and offered to move a running stack
    # off its own subnet. Excluding our network from Docker's list was not enough: Docker creates a
    # ROUTE for its own bridge, so the range came back through the route table instead.
    sandbox_path own-bridge-routed docker-collision
    use_temp_env "PROXY_SUBNET=172.28.0.0/16
PROXY_NETWORK=172.28.0.0/16"

    case "$(allocated_ranges)" in
        *172.28.0.0/16*) fail "our own bridge's route counted as somebody else's" ;;
        *) passed=$((passed + 1)) ;;
    esac

    out="$(check_subnet_collision 2>&1 <<< "n")"
    case "$out" in
        *collides*) fail "reported a collision with itself" ;;
        *) passed=$((passed + 1)) ;;
    esac
    assert_eq "" "$pending" "and planned no move"
fi

if test_case "a smaller range inside ours is still a collision"; then
    # Exact matches are the bridge; a real network inside the range is not, and must still be seen.
    sandbox_path own-bridge-routed docker-collision
    assert_succeeds cidr_overlap "172.28.0.0/16" "172.28.5.0/24"
    hits="$(overlaps_any "172.28.0.0/16" <<< "172.28.5.0/24")"
    assert_eq "172.28.5.0/24" "$hits" "a /24 inside our /16 still reports"
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

if test_case "a Windows zone becomes an IANA one"; then
    # Windows says "W. Europe Standard Time"; TZ takes "Europe/Berlin". The conversion needs .NET 6+,
    # which Windows PowerShell 5.1 does not have - so the container the wizard already runs in
    # answers it, and this is the branch that asks.
    sandbox_path linux dotnet-tz
    HOMESPOOL_HOST_DLL="$(mktemp "${TMPDIR:-/tmp}/hostdll.XXXXXX")"
    export HOMESPOOL_HOST_DLL

    HOMESPOOL_WINDOWS_TZ="W. Europe Standard Time"
    export HOMESPOOL_WINDOWS_TZ
    assert_eq "Europe/Berlin" "$(detect_timezone)" "asked the container rather than the container's clock"

    # The region is not decoration: it is what makes a Dane's .env say Copenhagen rather than Paris,
    # which behave identically and read very differently.
    HOMESPOOL_WINDOWS_TZ="Romance Standard Time"
    assert_eq "Europe/Paris" "$(detect_timezone)" "without a region, the zone's default"
    HOMESPOOL_WINDOWS_REGION=DK
    export HOMESPOOL_WINDOWS_REGION
    assert_eq "Europe/Copenhagen" "$(detect_timezone)" "with one, the local name"

    # An unrecognised zone leaves the ordinary detection to answer, rather than writing nothing.
    HOMESPOOL_WINDOWS_TZ="Not A Zone"
    case "$(detect_timezone)" in
        "") fail "an unknown zone produced an empty TZ" ;;
        *) passed=$((passed + 1)) ;;
    esac

    rm -f "$HOMESPOOL_HOST_DLL"
    unset HOMESPOOL_HOST_DLL HOMESPOOL_WINDOWS_TZ HOMESPOOL_WINDOWS_REGION
fi

if test_case "an image without the applet does not become the time zone"; then
    # This happened: the running image predated the applet, so --iana-timezone reached
    # WebApplication.CreateBuilder, the server started, failed migrating a database that was not
    # mounted, and a page of Serilog JSON was offered as the default time zone.
    sandbox_path linux dotnet-old-image
    HOMESPOOL_HOST_DLL="$(mktemp "${TMPDIR:-/tmp}/hostdll.XXXXXX")"
    HOMESPOOL_WINDOWS_TZ="W. Europe Standard Time"
    export HOMESPOOL_HOST_DLL HOMESPOOL_WINDOWS_TZ

    assert_eq "" "$(windows_timezone)" "log output is not a time zone"
    case "$(detect_timezone)" in
        *"@t"*|*"{"*) fail "JSON reached TZ" ;;
        *) passed=$((passed + 1)) ;;
    esac

    rm -f "$HOMESPOOL_HOST_DLL"
    unset HOMESPOOL_HOST_DLL HOMESPOOL_WINDOWS_TZ
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
USER_HOSTS=localhost
TZ=UTC" "PRINTER_HOST=set.lan" > /dev/null
    assert_eq "set.lan" "$(env_get PRINTER_HOST)" "from .env"
    assert_eq "localhost" "$(env_get USER_HOSTS)" "fell through to .env.example"
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
USER_HOSTS=x
PRINTER_HOST=second.lan" > /dev/null
    assert_eq "second.lan" "$(env_get PRINTER_HOST)" "reading takes the last"
    plan_set PRINTER_HOST third.lan
    apply >/dev/null 2>&1
    assert_eq "PRINTER_HOST=first.lan
USER_HOSTS=x
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
# --no-prompt and --no-overwrite
#
# The combination a systemd unit runs on every boot with no stamp file.
# ------------------------------------------------------------------------------------------------

if test_case "no-overwrite treats empty as a blank and a value as an answer"; then
    # PRINTER_HOST= is present and empty, and filling it in is the entire point on a board setting
    # itself up. SMTP_HOST= is empty *deliberately* - it means "no outgoing mail". Both are empty;
    # only one is an answer. Getting this backwards would either refuse to configure the Pi at all or
    # silently re-enable mail somebody turned off.
    use_temp_env "PRINTER_HOST=
SMTP_HOST=
USER_HOSTS=localhost" "PRINTER_HOST=
SMTP_HOST=
USER_HOSTS=already.chosen"
    no_overwrite=true

    plan_set PRINTER_HOST 192.168.13.238
    plan_set SMTP_HOST mail.example.com
    plan_set USER_HOSTS detected.local

    assert_contains "$pending" "PRINTER_HOST=192.168.13.238" "an empty key is a blank to fill"
    assert_contains "$pending" "SMTP_HOST=mail.example.com" "so is a deliberately empty one"
    case "$pending" in
        *USER_HOSTS*) fail "overwrote a key that already carried a value" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

if test_case "no-overwrite is not fooled by .env.example's defaults"; then
    # env_get falls back to the example, where every documented default is non-empty - so a check
    # written against it would treat every unset key as already answered and fill in nothing at all.
    use_temp_env "USER_HOSTS=localhost
TZ=UTC" "PRINTER_HOST=192.168.13.238"
    no_overwrite=true
    plan_set USER_HOSTS detected.local
    plan_set TZ Europe/Copenhagen
    assert_contains "$pending" "USER_HOSTS=detected.local" "absent from .env, so it is a blank"
    assert_contains "$pending" "TZ=Europe/Copenhagen" "likewise"
fi

if test_case "no-overwrite applies to --set as well"; then
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$repo_root/setup-env.sh" "$dir/"
    "$BASH" "$dir/setup-env.sh" --set PRINTER_HOST=first.lan >/dev/null 2>&1
    "$BASH" "$dir/setup-env.sh" --set PRINTER_HOST=second.lan --no-overwrite >/dev/null 2>&1
    assert_contains "$(grep '^PRINTER_HOST=' "$dir/.env")" "first.lan" "the existing value stood"
fi

if test_case "no-prompt answers from detection and fills only blanks"; then
    sandbox_path linux docker-collision
    use_temp_env "PRINTER_HOST=
USER_HOSTS=localhost
TZ=UTC
GO2RTC_USERNAME=
GO2RTC_PASSWORD=" "PRINTER_HOST=
USER_HOSTS=already.chosen"
    no_overwrite=true
    auto_answer >/dev/null 2>&1

    assert_contains "$pending" "PRINTER_HOST=192.168.13.238" "took the detected address"
    case "$pending" in
        *USER_HOSTS*) fail "overwrote the name somebody had already chosen" ;;
        *) passed=$((passed + 1)) ;;
    esac
    assert_contains "$pending" "GO2RTC_PASSWORD=" "generated the camera credential"
fi

if test_case "no-prompt is fatal when no address can be found"; then
    # The behaviour the systemd unit depends on. A run that exits 0 having done nothing is recorded
    # by RemainAfterExit=yes as *Finished, successfully* and never retried - which is how a board
    # ended up with a working network, no stack, and a cable plugged in five minutes too late.
    #
    # The bsd stubs offer no usable address once loopback and link-local are dropped.
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$repo_root/setup-env.sh" "$dir/"
    mkdir -p "$dir/bin"
    printf '#!/bin/sh\necho "127.0.0.1"\n' > "$dir/bin/hostname"
    printf '#!/bin/sh\nexit 1\n' > "$dir/bin/ip"
    chmod +x "$dir/bin"/*
    out="$(PATH="$dir/bin:$system_path" "$BASH" "$dir/setup-env.sh" --no-prompt --no-overwrite 2>&1)"
    status=$?
    assert_eq "1" "$status" "exits non-zero so a supervisor retries"
    assert_contains "$out" "no address" "and says why"
    if [ -f "$dir/.env" ]; then
        fail "wrote a .env despite having no address to put in it"
    else
        passed=$((passed + 1))
    fi
fi

if test_case "dry-run composes with the unattended flags"; then
    # "Show me what the board would do" before flashing a card, and the one combination where a
    # mistake is expensive to discover later.
    #
    # Driven through the stubs rather than the host's real network: in a container there is no
    # docker and the only address is inside 172.16/12, so --no-prompt correctly refuses and the
    # test would be asserting on the wrong path.
    sandbox_path linux docker-collision
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$repo_root/setup-env.sh" "$dir/"
    out="$("$BASH" "$dir/setup-env.sh" --no-prompt --no-overwrite --dry-run 2>&1)"
    assert_contains "$out" "192.168.13.238" "answered from detection"
    assert_contains "$out" "nothing written" "and said it wrote nothing"
    if [ -f "$dir/.env" ]; then
        fail "a dry run created .env"
    else
        passed=$((passed + 1))
    fi
fi

if test_case "no-prompt on every boot is idempotent"; then
    # The property that lets a unit call this unconditionally with no stamp file.
    sandbox_path linux docker-collision
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$repo_root/setup-env.sh" "$dir/"

    "$BASH" "$dir/setup-env.sh" --no-prompt --no-overwrite >/dev/null 2>&1
    assert_contains "$(cat "$dir/.env")" "PRINTER_HOST=192.168.13.238" "the first boot configured it"
    first="$(cat "$dir/.env")"

    out="$("$BASH" "$dir/setup-env.sh" --no-prompt --no-overwrite 2>&1)"
    assert_contains "$out" "Nothing to change" "the second boot plans nothing"
    assert_eq "$first" "$(cat "$dir/.env")" "and changes nothing"
fi

if test_case "the summary names a default only when there is one"; then
    # This line is what somebody reads before answering yes, so it is worth asserting on. It used to
    # say "(unset, default (empty))" - two brackets to report that a setting with no value has none.
    use_temp_env "PRINTER_HOST=
USER_HOSTS=localhost"
    plan_set PRINTER_HOST 192.168.13.238
    plan_set USER_HOSTS box.local
    out="$(summarise 2>&1)"

    assert_contains "$out" "(unset)  ->  192.168.13.238" "no default worth naming, so none is named"
    assert_contains "$out" "(unset, default localhost)  ->  box.local" "a real default still is"
    case "$out" in
        *"default (empty)"*) fail "the summary still nests brackets around nothing" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

# ------------------------------------------------------------------------------------------------
# Outgoing mail
#
# Every case here comes from configuring a real Mailpit and watching it fail.
# ------------------------------------------------------------------------------------------------

if test_case "inside a container it does not suggest the container id"; then
    # The Windows path runs in a one-off `docker run`, where `hostname` is the container id - so it
    # suggested "5d44b2605478.local" as the name to type into a browser.
    use_temp_env "USER_HOSTS=localhost"
    in_container() { return 0; }
    assert_eq "localhost" "$(suggested_user_host)" "no answer beats a meaningless one"
    assert_eq "" "$(machine_name)" "and there is no name to be had in there"

    # ...unless one is handed in from outside, which is what setup-env.ps1 does.
    HOMESPOOL_HOSTNAME=DESKTOP-7Q2 
    export HOMESPOOL_HOSTNAME
    # Verbatim: this machine knows nothing about that one's network, so every way of qualifying it
    # would be a guess about somebody else's DNS.
    assert_eq "DESKTOP-7Q2" "$(suggested_user_host)" "the Windows machine's own name, as given"
    unset HOMESPOOL_HOSTNAME
    unset -f in_container
fi

if test_case "a list already chosen is offered back whole"; then
    # The suggestion is one name, but the answer need not be - and re-running the wizard on a .env
    # that carries several must not propose replacing them with one. It reads the value, it does not
    # parse it.
    use_temp_env "USER_HOSTS=localhost" "USER_HOSTS=homespool.lan;homespool.local;192.168.1.50"
    assert_eq "homespool.lan;homespool.local;192.168.1.50" "$(suggested_user_host)" "offered verbatim"
fi

if test_case "the machine name is offered only when it resolves to a listed address"; then
    sandbox_path linux docker-collision
    # localhost resolves to 127.0.0.1, which is never on the candidate list - so it is not offered.
    HOMESPOOL_HOSTNAME=localhost
    export HOMESPOOL_HOSTNAME
    assert_eq "" "$(name_candidate "$(lan_addresses)")" "a name pointing off the list is not suggested"

    # A name resolving to an address that IS on the list is what earns a place.
    HOMESPOOL_HOSTNAME=elsewhere.invalid
    assert_eq "" "$(name_candidate "$(lan_addresses)")" "and neither is one that resolves nowhere"
    unset HOMESPOOL_HOSTNAME
    unset -f in_container 2>/dev/null || true
fi

if test_case "the name candidate claims only what was checked"; then
    # "survives a new DHCP lease" was the first wording and it overpromised. The resolve test uses
    # THIS machine's resolver, which includes mDNS, and answers for right now - it says nothing
    # about the router continuing to publish the name, or about a printer being able to resolve it.
    sandbox_path linux docker-collision
    HOMESPOOL_HOSTNAME=printbox
    export HOMESPOOL_HOSTNAME
    resolve_host() { echo "192.168.13.238"; }
    line="$(name_candidate "$(lan_addresses)")"
    unset -f resolve_host
    unset HOMESPOOL_HOSTNAME

    # A bare hostname now tries the network's own domain before .local. Whether anything is offered
    # depends on the machine running the suite, so this asserts the rule that matters: whatever comes
    # back is never a .local one.
    case "$line" in
        *.local*) fail "a .local name was offered for PRINTER_HOST" ;;
        *) passed=$((passed + 1)) ;;
    esac

    # A name from real DNS is, because that is the kind a printer could actually use.
    HOMESPOOL_HOSTNAME=printbox.lan
    export HOMESPOOL_HOSTNAME
    resolve_host() { echo "192.168.13.238"; }
    line="$(name_candidate "$(lan_addresses)")"
    unset -f resolve_host
    unset HOMESPOOL_HOSTNAME

    assert_contains "$line" "printbox.lan" "a DNS name is offered"
    assert_contains "$line" "resolves here to 192.168.13.238" "with what it resolved to, which was checked"
    case "$line" in
        *survives*) fail "still promising it survives a lease change" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

if test_case "a name is resolved to IPv4, not to whatever comes first"; then
    # The Pi's actual fault. `getent hosts homespool.lan` answered fdc2:74d8:1010::cd4 - correct, and
    # useless here: the candidate list is IPv4 by construction, so the comparison matched nothing and
    # the one name that worked was silently dropped. Reverse DNS had been right all along.
    sandbox_path linux dns-v6first
    assert_eq "192.168.13.183" "$(resolve_host homespool.lan)" "the A record, not the AAAA"

    HOMESPOOL_ADDRESSES="192.168.13.183	wlan0"
    export HOMESPOOL_ADDRESSES
    line="$(name_candidate "$(lan_addresses)")"
    unset HOMESPOOL_ADDRESSES
    assert_contains "$line" "homespool.lan" "so the name is offered"
    assert_contains "$line" "resolves here to 192.168.13.183" "against the address on the list"
fi

if test_case "a .local served by real DNS is accepted"; then
    # .local was only reserved for mDNS in 2013, and networks built before that serve it from
    # ordinary DNS - where a printer resolves it perfectly. Treating every .local as mDNS would
    # withhold a name that works, so the question is asked rather than assumed.
    sandbox_path linux docker-collision dns-local
    assert_succeeds resolves_in_dns "printbox.local"
    use_temp_env "PRINTER_HOST="
    out="$( (validate_printer_host "printbox.local" <<< "y") 2>&1 )"
    assert_says "$out" "served by ordinary DNS" "says it checked, and what it found"
fi

if test_case "with nothing to ask with, .local is treated as mDNS"; then
    # No dig, nslookup or host - which is every container, including the one the Windows path uses.
    # Warning wrongly costs a keystroke; approving wrongly costs a PRINTER_HOST no printer can reach,
    # frozen into a certificate.
    sandbox_path linux docker-collision
    resolves_in_dns "printbox.local"
    assert_eq "2" "$?" "reports that it could not tell"
    use_temp_env "PRINTER_HOST="
    out="$( (validate_printer_host "printbox.local" <<< "n") 2>&1 )"
    assert_says "$out" "no dig" "says why it could not check"
fi

if test_case "a hand-typed .local name is challenged too"; then
    # Excluding it from the offered list only covers the list. Typing it walks straight past, and it
    # is the answer somebody reaches for precisely because it resolves from their own machine.
    sandbox_path linux docker-collision dns-none
    use_temp_env "PRINTER_HOST="
    out="$( (validate_printer_host "printbox.local" <<< "n") 2>&1 )"
    status=$?
    assert_eq "1" "$status" "refused when the answer is no"
    assert_says "$out" "does not resolve in DNS" "says it checked, and what it found"

    # Still the operator's call, as with every other warning here. Two answers, because a name that
    # does not resolve is asked about as well - which this one does not, being mDNS.
    if validate_printer_host "printbox.local" >/dev/null 2>&1 <<'ANSWERS'
y
y
ANSWERS
    then passed=$((passed + 1)); else fail "refused an answer the operator insisted on"; fi
fi

if test_case "one machine gets one name"; then
    # PRINTER_HOST offered homespool.lan while USER_HOSTS suggested homespool.local - two names for
    # one box on one screen, because only the first was allowed to ask the network.
    sandbox_path linux dns-v6first
    use_temp_env "USER_HOSTS=localhost"
    HOMESPOOL_ADDRESSES="192.168.13.183	wlan0"
    export HOMESPOOL_ADDRESSES
    suggestion="$(suggested_user_host)"
    unset HOMESPOOL_ADDRESSES

    assert_eq "homespool.lan" "$suggestion" "the name the network publishes, same as PRINTER_HOST"
fi

if test_case "USER_HOSTS may be .local, because browsers do mDNS"; then
    # The opposite of the PRINTER_HOST rule, and the reason it is not one rule: what resolves
    # USER_HOSTS is a desktop, which does mDNS perfectly well. What resolves PRINTER_HOST is Buddy
    # firmware, which broadcasts mDNS but cannot resolve it.
    #
    # Which qualification wins depends on the machine - a network with a domain of its own beats
    # .local - so this asserts the part that is fixed: a bare name does not stay bare.
    # machine_name is overridden rather than trusted: in a container it correctly returns nothing, so
    # the honest answer there is localhost and the rule under test never runs.
    use_temp_env "USER_HOSTS=localhost"
    machine_name() { echo printbox; }
    case "$(suggested_user_host)" in
        *.*) passed=$((passed + 1)) ;;
        *) fail "a bare hostname was offered to a browser unqualified" ;;
    esac

    # And with nothing to go on at all, no answer beats a wrong one. Both sources are silenced:
    # reverse DNS is a real source now, so stubbing only machine_name no longer means "nothing".
    machine_name() { echo ""; }
    reverse_name() { echo ""; }
    assert_eq "localhost" "$(suggested_user_host)" "falls back rather than inventing a name"
fi

if test_case "smtp offers host.docker.internal instead of localhost"; then
    # localhost is the container, so a mail server on the machine is refused from in there - the
    # same mistake PRINTER_HOST is checked for, which SMTP_HOST was not.
    use_temp_env "SMTP_HOST=
SMTP_PORT=587"
    ask_smtp >/dev/null 2>&1 <<'ANSWERS'
y
localhost
y
587

ANSWERS
    assert_contains "$pending" "SMTP_HOST=host.docker.internal" "swapped for something reachable"
fi

if test_case "smtp keeps localhost if you insist"; then
    use_temp_env "SMTP_HOST=
SMTP_PORT=587"
    ask_smtp >/dev/null 2>&1 <<'ANSWERS'
y
localhost
n
587

ANSWERS
    assert_contains "$pending" "SMTP_HOST=localhost" "a warning, not a veto"
fi

if test_case "port 25 really means no encryption"; then
    # It was advertised as "25 for unencrypted" in .env.example and here, and set nothing - the
    # sender demands STARTTLS whenever implicit TLS is off, so port 25 failed at send time.
    use_temp_env "SMTP_HOST=
SMTP_PORT=587
SMTP_USE_IMPLICIT_TLS=false
SMTP_DISABLE_TLS=false"
    ask_smtp >/dev/null 2>&1 <<'ANSWERS'
y
mail.example.com
25

ANSWERS
    assert_contains "$pending" "SMTP_DISABLE_TLS=true" "encryption actually turned off"
    assert_contains "$pending" "SMTP_USE_IMPLICIT_TLS=false" "and not implicit TLS"
fi

if test_case "465 and 587 still settle both halves from one answer"; then
    use_temp_env "SMTP_HOST=
SMTP_PORT=587
SMTP_USE_IMPLICIT_TLS=false
SMTP_DISABLE_TLS=false"
    ask_smtp >/dev/null 2>&1 <<'ANSWERS'
y
mail.example.com
465

ANSWERS
    assert_contains "$pending" "SMTP_USE_IMPLICIT_TLS=true" "465 is implicit TLS"
    assert_contains "$pending" "SMTP_DISABLE_TLS=false" "and still encrypted"
fi

if test_case "an unusual port asks rather than assuming STARTTLS"; then
    # Mailpit on 1025 offers no encryption at all. Guessing STARTTLS is how that fails at send
    # time, with "does not support the STARTTLS extension", long after this question.
    use_temp_env "SMTP_HOST=
SMTP_PORT=587
SMTP_USE_IMPLICIT_TLS=false
SMTP_DISABLE_TLS=false"
    # Redirected to a file rather than captured with $( ), which would run ask_smtp in a subshell
    # and discard everything it planned - the same trap use_temp_env carries a warning about.
    ask_smtp > "$temp_env_dir/out" 2>&1 <<'ANSWERS'
y
host.docker.internal
1025
3

ANSWERS
    out="$(cat "$temp_env_dir/out")"
    assert_says "$out" "does not say how the" "said why it has to ask"
    assert_contains "$pending" "SMTP_PORT=1025" "kept the port"
    assert_contains "$pending" "SMTP_DISABLE_TLS=true" "and took the answer given"
fi

if test_case "no command is missing from the sandbox"; then
    # The guard the list needed. Three times a tool added to setup-env.sh was not added here, and
    # every time it appeared as some unrelated assertion failing - a red run pointing at the wrong
    # thing, which is the most expensive kind. This asserts on the symptom itself.
    sandbox_path linux docker-collision
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$repo_root/setup-env.sh" "$dir/"

    out="$("$BASH" "$dir/setup-env.sh" --no-prompt --no-overwrite 2>&1)"
    case "$out" in
        *"command not found"*)
            fail "a tool setup-env.sh needs is missing from sandbox_path: $(printf '%s' "$out" | grep -o '[a-z]*: command not found' | head -1)"
            ;;
        *) passed=$((passed + 1)) ;;
    esac

    # And the same for the interactive path's first question, which uses different tools again.
    out="$( (ask_printer_host < /dev/null) 2>&1 )"
    case "$out" in
        *"command not found"*) fail "a tool is missing from sandbox_path on the interactive path" ;;
        *) passed=$((passed + 1)) ;;
    esac
fi

if test_case "every string a person reads is translatable"; then
    # A lint, not a behaviour test. New prose gets added by writing say "..." like all the lines
    # around it, and an unmarked string is invisible - it works perfectly and is simply never
    # translated. Nothing else would ever catch that, so it is caught here.
    unmarked="$(grep -nE '^[[:space:]]+(say|warn|ask|ask_yes_no|ask_secret) "' "$repo_root/setup-env.sh" \
        | grep -v '\$(' || true)"
    if [ -n "$unmarked" ]; then
        fail "user-facing strings not marked for translation: $(printf '%s' "$unmarked" | head -3 | tr '\n' ' ')"
    else
        passed=$((passed + 1))
    fi
fi

if test_case "an untranslated build still speaks English"; then
    # The fallback is what makes marking free: no catalogue, no locale, or a missing string, and the
    # English written in the source is what appears. Asserted because it is the ordinary case for
    # every user today, and would be the thing to break.
    sandbox_path linux docker-collision
    dir="$(mktemp -d "${TMPDIR:-/tmp}/setup-env-e2e.XXXXXX")"
    cp "$repo_root/.env.example" "$repo_root/setup-env.sh" "$dir/"
    out="$(TEXTDOMAINDIR=/nonexistent "$BASH" "$dir/setup-env.sh" --no-prompt --no-overwrite --dry-run 2>&1)"
    assert_says "$out" "This will change" "English with no catalogue at all"
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

if test_case "input ending is not an answer"; then
    # It used to fall through to the default, so a closed stdin answered every question with its
    # default and then said yes to "Write these" - a run nobody was present for, ending in a write.
    # It is also the way out when Ctrl+C cannot get through the Windows launcher.
    # ask still yields the default, because every plan_set site expects one - it reports the failure
    # through its status instead. Exiting from ask itself was the first attempt and was worse: it is
    # called inside a command substitution, so it ended only the subshell and ask_yes_no then spun
    # for ever asking a stream that had nothing left to give.
    out="$(ask "Which" "192.168.13.238" < /dev/null 2>/dev/null)"
    status=$?
    assert_eq "1" "$status" "the failure is reported"
    assert_eq "192.168.13.238" "$out" "and the default still comes back for callers that want one"

    # The confirmation is the caller that must not accept a default.
    out="$( (ask_yes_no "Write these" y < /dev/null) 2>&1 )"
    status=$?
    assert_eq "1" "$status" "stops rather than confirming a write nobody asked for"
    assert_says "$out" "nothing was written" "and says so"
    case "$out" in
        *"Please answer y or n"*"Please answer y or n"*) fail "looping on a closed input" ;;
        *) passed=$((passed + 1)) ;;
    esac
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
