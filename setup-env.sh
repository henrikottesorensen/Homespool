#!/usr/bin/env bash
#
# Writes the handful of .env settings a deployment cannot guess, and leaves the rest alone.
#
#   ./setup-env.sh                          # ask, show what would change, then write
#   ./setup-env.sh --set PRINTER_HOST=...   # set keys directly, no questions
#   ./setup-env.sh --no-prompt              # answer from what this machine can detect
#   ./setup-env.sh --no-overwrite           # only fill in settings that have no value yet
#   ./setup-env.sh --dry-run                # say what would change and write nothing
#
# The last two are independent, and the unattended case wants BOTH:
#
#   ./setup-env.sh --no-prompt --no-overwrite
#
# --no-prompt alone re-detects and overwrites, which on every boot means a moved DHCP lease silently
# rewriting PRINTER_HOST under a certificate that was minted once and cannot follow it.
#
# Every variable in compose.yaml carries its own default, so .env only ever needs to hold what
# differs. That is what makes this safe to run on a file somebody has already edited: it patches the
# keys it asked about, line by line, and every other byte - comments, blank lines, keys it has never
# heard of, a hand-set PRINTER_TLS=false - survives untouched. It never regenerates the file.
#
# PRINTER_TLS is deliberately not offered. Its purpose is reading the printer protocol in the clear,
# so everyone who wants it is already editing this file by hand, and a wizard that offered it would
# mostly succeed at turning it off by accident.
#
# The questions are the small part. The checks are the point: an address inside a Docker network is
# an address no printer can reach, and it is frozen into a certificate on the first start.
#
# ---------------------------------------------------------------------------------------------
# PLATFORM CONSTRAINTS - read before "tidying" anything here
# ---------------------------------------------------------------------------------------------
#
# Runs on macOS, WSL and Linux. Two rules follow from that, and both are currently enforced by
# nothing except this comment:
#
#   1. BASH 3.2. macOS still ships bash 3.2 for licensing reasons, so nothing here may use a bash 4
#      feature - no `declare -A`, no `mapfile`/`readarray`, no `${var,,}` or `${var^^}`, no
#      globstar, and no `printf '%(...)T'` (4.2+, and the tempting replacement for `date`). At the
#      time of writing the file uses none of these, which is not luck.
#
#   2. NOTHING BUT A SHELL AND COREUTILS. This is what a deployment runs *before* it has a stack, on
#      a machine that may have containers and little else - so no python, no dotnet, no jq. Prefer
#      builtins; where an external is needed, keep to POSIX options that GNU and BSD both take
#      (`date +%Y%m%d-%H%M%S`, not `date --iso-8601=seconds`).
#
# Both rules fail the same way: fine on the machine of whoever changed it, broken on somebody
# else's, and not until a user hits it. If either has to be broken, say so here.
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ------------------------------------------------------------------------------------------------
# Translation
#
# Every string a person reads is marked $"..." - bash's gettext form, which looks the message up in
# the catalogue for the current locale and falls back to the English written here when there is no
# translation, no catalogue, or no locale. That fallback is why marking the strings costs nothing:
# with no po/locale directory at all this behaves exactly as it did before.
#
# To add a language:
#
#     bash --dump-po-strings setup-env.sh > po/setup-env.pot     # refresh the template
#     msginit -i po/setup-env.pot -l da_DK -o po/da.po           # start a translation
#     msgfmt -o po/locale/da/LC_MESSAGES/homespool-setup.mo po/da.po
#
# The compiled .mo files are committed, because this script is run straight from a clone and there is
# no build step to compile them at install time.
#
# KNOWN NOT TO WORK IN THE CONTAINER, deliberately. The image has only the C, C.utf8 and POSIX
# locales, and glibc ignores LANGUAGE under C - so the Windows path, which runs this inside the
# image, is English whatever the catalogue says. Generating a locale there costs 19 MB and was
# judged not worth it: WSL2 is the documented route for that machine anyway, and there the host's
# own locale applies normally.
export TEXTDOMAIN=homespool-setup
export TEXTDOMAINDIR="$repo_root/po/locale"
env_file="$repo_root/.env"
example_file="$repo_root/.env.example"

dry_run=false

# Answer from detection instead of asking, and never change a key that already carries a value.
# Independent on purpose - all four combinations mean something, and the boot-time case wants both.
no_prompt=false
no_overwrite=false

# KEY=VALUE pairs from --set, held until every flag has been read. See the argument loop.
set_args=""
# Whether any --set was given, which is what turns the questions off. Tracked separately from the
# pending list because `--set` with a value identical to the current one leaves that list empty, and
# that must still mean "you asked for non-interactive" rather than "ask me everything".
non_interactive=false

# Pending changes, as KEY=VALUE lines. Applied only after the summary is confirmed, so that a run
# abandoned halfway leaves .env exactly as it was found rather than half-written.
pending=""

# Values never echoed back to the terminal or into the summary. The summary has to be reviewable by
# the person typing, and a password on screen is not something they asked to publish to whoever is
# standing behind them.
secret_keys="SMTP_PASSWORD GO2RTC_PASSWORD"

# ------------------------------------------------------------------------------------------------
# Reading what is already there
# ------------------------------------------------------------------------------------------------

# The current value of a key, from .env if it is set there, otherwise from .env.example - which is
# the same order compose resolves them in, since an unset key falls through to the ${KEY:-default}
# in compose.yaml and .env.example documents those defaults.
#
# Last assignment wins, matching how a shell and docker compose both read the file: an operator who
# has appended a second PRINTER_HOST= at the bottom means the bottom one.
file_get() {
    local file="$1" key="$2" value
    [ -f "$file" ] || return 0
    value="$(awk -v key="$key" '
        index($0, key "=") == 1 { value = substr($0, length(key) + 2) }
        END { print value }
    ' "$file")"
    compose_unescape "$value"
}

# A dollar in .env is compose's, not yours. `SMTP_PASSWORD=p@ss$xword` reaches the container as
# `p@ss` - the rest is read as a variable name, found undefined, and dropped in silence, so the
# server reports an authentication failure and the file on disk looks exactly right. `$$` is the
# escape that survives; verified by reading the value back out of a running container rather than out
# of `docker compose config`, which re-escapes its own output and cannot answer this question.
#
# So values are escaped on the way in and unescaped on the way out, and the pair has to stay
# symmetrical - the summary compares what is in the file against what was typed.
compose_escape() {
    echo "${1//\$/\$\$}"
}

compose_unescape() {
    echo "${1//\$\$/\$}"
}

env_get() {
    local value
    value="$(file_get "$env_file" "$1")"
    if [ -z "$value" ] && ! key_present "$env_file" "$1"; then
        value="$(file_get "$example_file" "$1")"
    fi
    echo "$value"
}

# Deliberately distinct from "has a non-empty value". SMTP_HOST= is a supported configuration that
# means "no outgoing mail", and treating that as absent would make the wizard re-offer .env.example's
# default to somebody who has already deliberately cleared it.
key_present() {
    local file="$1" key="$2"
    [ -f "$file" ] || return 1
    awk -v key="$key" 'index($0, key "=") == 1 { found = 1 } END { exit found ? 0 : 1 }' "$file"
}

# Whether a key already carries a real answer, as opposed to being absent or blank.
#
# The distinction is the whole of --no-overwrite and it is easy to get backwards. A seeded .env has
# `PRINTER_HOST=` - present, and empty - and filling that in is the entire point on a board setting
# itself up. But `SMTP_HOST=` is empty *deliberately*, meaning "no outgoing mail". Both are empty;
# only one is an answer. So: empty counts as a blank to fill, and only a non-empty value is
# protected, which is why this reads .env directly rather than through env_get - env_get falls back
# to .env.example, and every documented default there is non-empty.
env_value_set() {
    [ -n "$(file_get "$env_file" "$1")" ]
}

plan_set() {
    local key="$1" value="$2"

    # Applied to every source, including --set. Naming a value explicitly is an instruction, so
    # exempting --set is tempting - but "apply these defaults without clobbering anything" is a
    # useful thing to be able to say, and one rule is easier to reason about than a rule with an
    # exception. Drop the flag to force it.
    if $no_overwrite && env_value_set "$key"; then
        return 0
    fi

    # A change to the value it already holds is not a change. Filtering here rather than at the
    # summary keeps "nothing to do" a real, reachable outcome instead of a diff full of no-ops.
    #
    # The key must also already be IN the file: a value equal to .env.example's default still has to
    # be written when the line is absent, or an answer that happens to match the default is silently
    # dropped and the operator is told nothing changed.
    if key_present "$env_file" "$key" && [ "$(env_get "$key")" = "$value" ]; then
        return 0
    fi
    pending="$pending$key=$value
"
}

# ------------------------------------------------------------------------------------------------
# Addresses
#
# Enough IPv4 arithmetic to answer one question: is this address, or this network, inside one of
# Docker's. Everything below is unsigned 32-bit integers in bash's 64-bit arithmetic, so nothing
# here can overflow.
# ------------------------------------------------------------------------------------------------

ip_to_int() {
    local a b c d
    IFS=. read -r a b c d <<< "$1"
    # A malformed address arithmetics to garbage rather than failing, so it is rejected here where
    # the caller can still say something useful about it.
    case "$1" in
        *[!0-9.]*|"") return 1 ;;
    esac
    [ -n "${d:-}" ] || return 1
    echo $(( (a << 24) + (b << 16) + (c << 8) + d ))
}

# Two CIDRs overlap when, masked to the *shorter* of their two prefixes, they are the same network.
# The shorter prefix is the containing one, so this catches a /24 inside a /16 as well as two equal
# ranges - which matters because Docker hands out /16s and a home LAN is usually a /24.
cidr_overlap() {
    local a="$1" b="$2"
    local a_base a_bits b_base b_bits bits mask
    a_base="$(ip_to_int "${a%%/*}")" || return 1
    b_base="$(ip_to_int "${b%%/*}")" || return 1
    a_bits="${a##*/}"
    b_bits="${b##*/}"
    bits=$(( a_bits < b_bits ? a_bits : b_bits ))
    mask=$(( 0xFFFFFFFF << (32 - bits) & 0xFFFFFFFF ))
    [ $(( a_base & mask )) -eq $(( b_base & mask )) ]
}

ip_in_cidr() {
    cidr_overlap "$1/32" "$2"
}

# Whether a CIDR overlaps any of the CIDRs on stdin, one per line. Four callers used to spell this
# loop out, and each had its own way of skipping blanks and swallowing the error from a malformed
# range - which is three chances to get it subtly different for no gain.
#
# Prints the ones it hit, so a caller that wants to name them in a warning does not have to run the
# comparison a second time to find out which.
overlaps_any() {
    local subject="$1" cidr hit=1
    while read -r cidr; do
        [ -n "$cidr" ] || continue
        if cidr_overlap "$subject" "$cidr" 2>/dev/null; then
            echo "$cidr"
            hit=0
        fi
    done
    return $hit
}

# Everything already spoken for on this machine, for the question "is this range free to move into":
# Docker's allocations EXCEPT our own, plus the host's own routes. Ours is excluded because a stack
# is not colliding with itself, and proposing a move off a range only it uses would be nonsense.
allocated_ranges() {
    local ours
    ours="$(our_compose_subnets)"

    # The host routes have to be filtered too, and missing that made the check accuse the stack of
    # colliding with itself: Docker creates a route for its own bridge, so 172.28.0.0/16 appears in
    # the route table BECAUSE our network exists, and excluding it from the Docker list alone left it
    # arriving by the other path. On the Pi that read as "172.28.0.0/16 collides with 172.28.0.0/16"
    # and offered to move a running stack off its own subnet.
    #
    # Exact matches only. A route equal to one of our subnets is the bridge itself; a SMALLER range
    # inside it is somebody's real network and still worth reporting.
    {
        docker_subnets_excluding_ours
        host_routes
    } | while IFS= read -r range; do
        [ -n "$range" ] || continue
        case "
$ours
" in
            *"
$range
"*) continue ;;
        esac
        echo "$range"
    done
}

# The subnets of this stack's own compose network, by label rather than by name - the project name
# comes from the directory, so a worktree or a -p flag changes it.
our_compose_subnets() {
    local ours id
    command -v docker >/dev/null 2>&1 || return 0
    ours="$(docker network ls --filter label=com.docker.compose.network=homespool -q 2>/dev/null || true)"
    for id in $ours; do
        docker network inspect "$id" -f '{{range .IPAM.Config}}{{println .Subnet}}{{end}}' 2>/dev/null || true
    done | grep -E '^[0-9]+\.' || true
}

# EVERY subnet Docker has allocated on this machine, one CIDR per line, this stack's own included -
# because an address inside our own bridge is exactly as unreachable to a printer as one inside
# anybody else's. The list that leaves ours out answers a different question; see below.
#
# Cached, because it is consulted once per candidate address and again for every validation, and
# each call is a docker inspect per network - which on a machine with a few stacks is a visible
# pause in the middle of a question.
docker_subnets_cache=""
docker_subnets_cached=false

docker_subnets() {
    if $docker_subnets_cached; then
        echo "$docker_subnets_cache"
        return 0
    fi
    docker_subnets_cache="$(docker_subnets_uncached)"
    docker_subnets_cached=true
    echo "$docker_subnets_cache"
}

# The same list without this stack's own network, which is a DIFFERENT question and was the same
# list for too long. Whether a range is free to move into must ignore our own - a stack does not
# collide with itself. Whether an ADDRESS is reachable must not: on the Pi the host's own end of our
# compose bridge, 172.28.0.1 on br-2deab6694565, was offered as somewhere a printer could reach,
# because the range it sits in had been excluded for the other question's benefit.
#
# Matched by compose label rather than by name: the project name comes from the directory, so a
# worktree or a -p flag changes it.
docker_subnets_excluding_ours() {
    local ours id
    command -v docker >/dev/null 2>&1 || return 0
    ours="$(docker network ls --filter label=com.docker.compose.network=homespool -q 2>/dev/null || true)"

    for id in $(docker network ls -q 2>/dev/null || true); do
        case " $ours " in
            *" $id "*) continue ;;
        esac
        docker network inspect "$id" -f '{{range .IPAM.Config}}{{println .Subnet}}{{end}}' 2>/dev/null || true
    done | grep -E '^[0-9]+\.' || true
}

docker_subnets_uncached() {
    command -v docker >/dev/null 2>&1 || return 0

    local id
    for id in $(docker network ls -q 2>/dev/null || true); do
        docker network inspect "$id" -f '{{range .IPAM.Config}}{{println .Subnet}}{{end}}' 2>/dev/null || true
    done | grep -E '^[0-9]+\.' || true
}

# Networks this host already routes to, one CIDR per line. The point is the ranges Docker does NOT
# know about - a VPN or an office LAN on 172.28.x - because that collision is the silent one: the
# stack comes up perfectly and the real network simply stops being reachable from this machine.
host_routes() {
    if command -v ip >/dev/null 2>&1; then
        ip -4 route show 2>/dev/null | awk '$1 ~ /^[0-9]+\./ { print $1 }' \
            | awk '/\// { print; next } { print $0 "/32" }'
        return 0
    fi

    # BSD netstat abbreviates a destination to its significant octets - "192.168.13" for a /24, and
    # "224.0.0/4" for a range whose prefix is not a whole number of them - so the missing octets have
    # to be put back and, where there is no prefix at all, counted out of what is there. Without this
    # every route reads as a /32 and a collision with a whole LAN goes unnoticed.
    netstat -rn -f inet 2>/dev/null | awk '
        $1 ~ /^[0-9]+[0-9.]*(\/[0-9]+)?$/ {
            dest = $1
            bits = ""
            if (dest ~ /\//) {
                bits = substr(dest, index(dest, "/") + 1)
                dest = substr(dest, 1, index(dest, "/") - 1)
            }
            n = split(dest, octets, ".")
            for (i = n + 1; i <= 4; i++) dest = dest ".0"
            print dest "/" (bits == "" ? n * 8 : bits)
        }'
}

# Candidate addresses for PRINTER_HOST, best first.
#
# The leading candidate is the source address the kernel would use to reach the wider world, which on
# a machine with several interfaces is the one actually carrying traffic. Everything else this host
# holds follows it, because a print server on a network with no default route is an ordinary
# deployment and that first answer is empty there.
# Running under WSL, where `ip route get` answers about the wrong machine.
#
# WSL2 sits behind its own NAT'd virtual switch, so detection there returns the VM's 172.x address
# just as it would inside a container - and that address is useless to a printer. Windows is where
# the LAN address lives.
#
# The interop file is the reliable marker: it is what lets a Linux process here execute Windows
# binaries, which is both how WSL is recognised and how the next function does its work, so a
# machine that has one without the other cannot arise. `microsoft` in the kernel release is the
# fallback for WSL1 and for a kernel built without binfmt registered.
is_wsl() {
    [ -e /proc/sys/fs/binfmt_misc/WSLInterop ] && return 0
    case "$(uname -r 2>/dev/null)" in
        *[Mm]icrosoft*) return 0 ;;
    esac
    return 1
}

# Windows' own addresses, asked of Windows, from inside WSL.
#
# This is the whole reason the WSL case needs no PowerShell wrapper and no container: WSL can run
# Windows binaries directly, so the same script that works everywhere else can simply ask. It is the
# route that matters on a machine where Docker Desktop will not install - Windows 10 LTSC 2021 is
# pinned to build 19044 and Docker Desktop wants 19045 - because there, Docker Engine inside WSL is
# not a preference but the only arrangement available.
#
# Get-NetIPAddress rather than ipconfig, whose output is localised: "IPv4 Address" is translated on
# a German or Danish install, so a parser written against an English one silently finds nothing.
# The trailing carriage returns are Windows'; strip them or every address fails to parse as one.
windows_addresses() {
    # Two filters, both learned from a real machine offering two addresses it should not have.
    #
    # ADAPTERS THAT ARE UP. A disconnected VPN adapter still reports the address it was configured
    # with - a ProtonVPN tunnel that was not even running contributed 10.2.0.2 - and no rule based on
    # the address itself could reject that, since 10.2.0.0/24 is a perfectly ordinary LAN.
    #
    # NOT vEthernet. Those are host-side virtual switches: "vEthernet (WSL)", "vEthernet (Default
    # Switch)", and whatever Hyper-V adds. A printer cannot reach any of them, and the WSL one is
    # especially galling because it exists *because* of how this script is being run.
    #
    # Falls back to the unfiltered list if the filtered one comes back empty, so an unusual adapter
    # arrangement degrades to "too many choices" rather than "none".
    local filtered
    filtered="$(windows_query '
        $up = Get-NetAdapter |
            Where-Object { $_.Status -eq "Up" -and $_.Name -notlike "vEthernet*" } |
            Select-Object -ExpandProperty ifIndex
        Get-NetIPAddress -AddressFamily IPv4 |
            Where-Object { $up -contains $_.InterfaceIndex } |
            ForEach-Object { "$($_.IPAddress)`t$($_.InterfaceAlias)" }')"

    if [ -n "$filtered" ]; then
        echo "$filtered"
    else
        windows_query 'Get-NetIPAddress -AddressFamily IPv4 |
            ForEach-Object { "$($_.IPAddress)`t$($_.InterfaceAlias)" }'
    fi
}

# One place that knows how to ask Windows a question and hand back plain lines. The carriage returns
# are Windows'; leave them on and every address fails to parse as one.
windows_query() {
    powershell.exe -NoProfile -NonInteractive -Command "$1" 2>/dev/null | tr -d '\r'
}

lan_addresses() {
    {
        # Supplied from outside, because this is running somewhere that cannot see the answer.
        #
        # That is Windows: the LAN address exists only on the Windows host, and neither a container
        # nor WSL2 can reach it - WSL2 is NAT'd behind its own virtual switch, so `ip route get`
        # there returns the VM's 172.x address exactly as it would in a container. setup-env.ps1
        # asks Windows and passes the list in here.
        #
        # A list, not an answer: everything below still applies to it, so a vEthernet address from
        # WSL or Hyper-V is filtered out by the same rule that filters Docker's own, and the ranking
        # and validation are unchanged. This supplies a fact the script cannot obtain, and decides
        # nothing.
        if [ -n "${HOMESPOOL_ADDRESSES:-}" ]; then
            # Two accepted shapes, and the distinction matters because interface names contain
            # spaces - "vEthernet (WSL)" would be shredded by the space split below.
            #
            # One address per LINE, optionally followed by a tab and its interface, is the rich form
            # setup-env.ps1 sends. A single line of space- or comma-separated addresses is the plain
            # form, kept because anything already passing this variable by hand uses it.
            #
            # $'\n' rather than "$(printf '\n')": command substitution strips trailing newlines, so
            # the latter is the EMPTY string, the pattern becomes ** and matches everything - which
            # sent every plain space-separated list down the newline branch, unsplit.
            case "$HOMESPOOL_ADDRESSES" in
                *$'\n'*) echo "$HOMESPOOL_ADDRESSES" ;;
                *) echo "$HOMESPOOL_ADDRESSES" | tr ' ,' '\n\n' ;;
            esac
        elif is_wsl; then
            windows_addresses
        elif command -v ip >/dev/null 2>&1; then
            # `dev` and `src` from the same line, so the preferred candidate arrives already named.
            ip -4 route get 1.1.1.1 2>/dev/null \
                | sed -n 's/.*dev \([^ ]*\).*src \([0-9.]*\).*/\2\t\1/p'
            # -o keeps each address on one line: "2: eth0    inet 192.168.13.238/24 brd ...".
            ip -4 -o addr show 2>/dev/null | awk '{ split($4, a, "/"); print a[1] "\t" $2 }'
            # No names here, and none available - this is the fallback for a machine without
            # iproute2 at all, which is exactly where nothing else can be asked either.
            hostname -I 2>/dev/null | tr ' ' '\n'
        else
            local iface
            iface="$(route -n get default 2>/dev/null | awk '/interface:/{print $2}')"
            [ -n "$iface" ] && printf '%s\t%s\n' "$(ipconfig getifaddr "$iface" 2>/dev/null)" "$iface"
            # The interface name heads its own block and the addresses follow it, so the name has to
            # be carried down rather than read off the same line.
            ifconfig 2>/dev/null | awk '
                /^[a-z0-9]+:/ { iface = substr($1, 1, length($1) - 1) }
                /^[[:space:]]*inet / { print $2 "\t" iface }'
        fi
    } | grep -E '^[0-9]+\.' | filter_unusable | dedupe
}

# The ranges a printer cannot route to, which is what an offered address is checked against.
#
# Normally these are the subnets Docker has actually allocated, asked of the daemon - because
# 172.16/12 is legitimate space somebody may genuinely run their house on, and refusing to offer
# their real address would be wrong in a way they could not argue with.
#
# But that only holds while the daemon answers. With docker absent, stopped, or not permitting this
# user, the query returns nothing and the check silently passes everything - including 172.17.0.1,
# the single address most likely to be wrong and the one that gets frozen into a certificate. An
# empty answer is reliably "could not ask" rather than "nothing allocated", because a working daemon
# always has at least the default bridge.
#
# So the fallback is the Pi's rule: exclude Docker's whole default pool, and say why. Conservative
# and loud, on the same reasoning the Pi's first-boot path had for excluding the block outright -
# and here the operator can still type an address by hand, so nothing is actually blocked.
unreachable_ranges() {
    local subnets
    subnets="$(docker_subnets)"
    if [ -n "$subnets" ]; then
        echo "$subnets"
        return 0
    fi
    # A FILE, not a variable. unreachable_ranges is called from inside command substitutions -
    # $(lan_addresses) and friends - so a flag set here lives in a subshell and dies with it, and the
    # same three-line explanation was printed three times in one run.
    if [ ! -e "$docker_warning_marker" ]; then
        : > "$docker_warning_marker" 2>/dev/null || true
        # Two different situations, and blaming the daemon in the first one is nonsense: on the
        # Windows path this script runs INSIDE a container, which has no docker CLI and no socket,
        # so of course it cannot ask - while Docker is plainly working, since it is running this.
        if in_container; then
            say $"  Running inside a container, so Docker's own ranges cannot be listed from here. Excluding 172.16.0.0/12 instead, which covers them. If your LAN genuinely lives there, type the address rather than picking from the list."
        else
            warn $"Could not ask Docker which ranges it has allocated - is it installed and running? Falling back to excluding 172.16.0.0/12 entirely. If your LAN genuinely lives there, type the address rather than picking from the list."
        fi
    fi
    echo "172.16.0.0/12"
}

# Per-run, per-process, and cleaned up on the way out: see unreachable_ranges for why it cannot be
# a variable.
docker_warning_marker="${TMPDIR:-/tmp}/setup-env-docker-warned.$$"
trap 'rm -f "$docker_warning_marker"' EXIT

# Whether this is running inside a container rather than on the host. /.dockerenv is written by
# Docker itself; the cgroup path covers runtimes that do not, and Podman.
in_container() {
    [ -e /.dockerenv ] && return 0
    grep -qE '(docker|containerd|podman|kubepods)' /proc/1/cgroup 2>/dev/null && return 0
    return 1
}

# Loopback and link-local are never reachable by a printer either, and need no daemon to recognise.
filter_unusable() {
    local subnets addr
    subnets="$(unreachable_ranges)"
    local line
    while IFS= read -r line; do
        [ -n "$line" ] || continue
        # Everything downstream is keyed on the address; the interface name rides along for display
        # only, so every test here reads the first field and every echo passes the whole line.
        addr="${line%%	*}"
        case "$addr" in
            127.*|169.254.*|0.0.0.0) continue ;;
        esac
        overlaps_any "$addr/32" <<< "$subnets" >/dev/null || echo "$line"
    done
}

# On the address, not the whole line: the same address can arrive twice with and without a name -
# `ip route get` names it, `ip -o addr show` names it again, and a bare fallback does not.
dedupe() {
    awk -F'\t' '!seen[$1]++'
}

# The IANA name for the Windows zone the launcher passed in, or nothing.
#
# Windows reports "W. Europe Standard Time"; TZ takes "Europe/Berlin". The conversion is one .NET
# call and this container is the only .NET within reach - Windows PowerShell 5.1 is .NET Framework
# 4.8, which has no TryConvertWindowsIdToIanaId, and a machine that cannot install Docker Desktop has
# no other runtime either. So the application answers it, as a one-shot argument.
#
# Shipping a mapping table here instead was the alternative, and it would be wrong the first time a
# zone changed and maintained by hand against data ICU already has.
#
# HOMESPOOL_WINDOWS_REGION is passed too because it changes the answer: "Romance Standard Time" alone
# is Europe/Paris, and with DK it is Europe/Copenhagen. Identical behaviour, and a Dane reading
# Europe/Paris in their own .env would reasonably think it a mistake.
windows_timezone() {
    [ -n "${HOMESPOOL_WINDOWS_TZ:-}" ] || return 0
    command -v dotnet >/dev/null 2>&1 || return 0
    # Where the image puts it. Overridable so the suite can point at a stub - the path is a fact
    # about the container, not about this script, and hard-coding it made the branch untestable.
    local dll="${HOMESPOOL_HOST_DLL:-/app/Homespool.Host.dll}"
    [ -f "$dll" ] || return 0

    # The output is CHECKED, not trusted, and this is not caution for its own sake. An image built
    # before the applet existed does not recognise --iana-timezone: it hands the argument to
    # WebApplication.CreateBuilder, STARTS THE SERVER, fails migrating a database that is not mounted,
    # and prints a page of JSON. That was offered as the default time zone, log lines and all.
    #
    # So: bounded in time, because a server that starts will sit there; last line only; and it has to
    # look like an IANA name - Area/Location, no spaces, no braces - or it is discarded. UTC is not
    # accepted either, since that is what an unset TZ already means and detection should carry on.
    local runner="" out
    command -v timeout >/dev/null 2>&1 && runner="timeout 10"

    out="$($runner dotnet "$dll" --iana-timezone \
        "$HOMESPOOL_WINDOWS_TZ" "${HOMESPOOL_WINDOWS_REGION:-}" 2>/dev/null | tail -1)" || true

    printf '%s\n' "$out" | grep -E '^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+)+$' | head -1
}

detect_timezone() {
    # Asked first, because in a container /etc/localtime is UTC and says so confidently - it would
    # answer, wrongly, before anything else got a chance.
    local from_windows
    from_windows="$(windows_timezone)"
    if [ -n "$from_windows" ]; then
        echo "$from_windows"
        return 0
    fi

    # The symlink is the reliable source on both platforms and needs no package: macOS points it into
    # /var/db/timezone/zoneinfo, Linux into /usr/share/zoneinfo, and the zone name is whatever
    # follows. /etc/timezone is the fallback for a Linux box where the symlink has been replaced by a
    # copy of the file, which some minimal images do.
    local link
    link="$(readlink /etc/localtime 2>/dev/null || true)"
    case "$link" in
        */zoneinfo/*) echo "${link##*/zoneinfo/}"; return 0 ;;
    esac
    [ -f /etc/timezone ] && head -1 /etc/timezone && return 0
    echo UTC
}

# ------------------------------------------------------------------------------------------------
# Asking
# ------------------------------------------------------------------------------------------------

# Prompts go to stderr so that a caller can capture an answer without capturing the question.
ask() {
    local prompt="$1" default="${2:-}" answer
    if [ -n "$default" ]; then
        printf '%s [%s]: ' "$prompt" "$default" >&2
    else
        printf '%s: ' "$prompt" >&2
    fi

    # END OF INPUT IS NOT AN ANSWER, and saying so needs care: ask is called from inside a command
    # substitution, so `exit` here would end only the subshell. The first attempt did exactly that
    # and ask_yes_no spun for ever printing "Please answer y or n" at a stream that had nothing left
    # to give - worse than the behaviour it replaced.
    #
    # So it still yields the default, which is what every plan_set site expects, and reports the
    # failure through its exit status for the one caller that must not accept a default: the
    # confirmation. Otherwise a closed stdin answers every question and then says yes to writing.
    if ! read -r answer; then
        echo "$default"
        return 1
    fi
    echo "${answer:-$default}"
}

ask_secret() {
    local prompt="$1" answer
    printf '%s: ' "$prompt" >&2
    # read -s is a bashism this script can rely on, but stty is what keeps the terminal sane if the
    # read is interrupted - without the trap a ^C here leaves the operator typing blind in their own
    # shell afterwards.
    trap 'stty echo 2>/dev/null || true' INT
    stty -echo 2>/dev/null || true
    read -r answer || true
    stty echo 2>/dev/null || true
    trap - INT
    echo >&2
    echo "$answer"
}

ask_yes_no() {
    local prompt="$1" default="$2" answer
    while :; do
        # Propagated, not ignored: this is the caller for which a default is not good enough. On a
        # closed input it stops rather than looping, and rather than confirming a write nobody asked
        # for. It is also the way out when Ctrl+C cannot get through - Ctrl+D, or Ctrl+Z on Windows.
        if ! answer="$(ask "$prompt (y/n)" "$default")"; then
            echo >&2
            echo "setup-env.sh: input ended - nothing was written." >&2
            exit 1
        fi
        case "$answer" in
            [Yy]|[Yy][Ee][Ss]) return 0 ;;
            [Nn]|[Nn][Oo]) return 1 ;;
            *) echo "  Please answer y or n." >&2 ;;
        esac
    done
}

# How wide to wrap. Asked once, because tput spawns a process and these are called per paragraph.
#
# COLUMNS is not exported to scripts by most shells, so tput is the real source and 80 is the
# fallback for a pipe, a CI log or a terminal too odd to ask. Clamped at both ends: below 40 the
# output is unreadable whatever we do, and above 100 long prose lines are harder to follow than
# short ones, so a very wide terminal is not filled edge to edge.
terminal_width_cache=""

terminal_width() {
    if [ -z "$terminal_width_cache" ]; then
        terminal_width_cache="${COLUMNS:-}"
        [ -n "$terminal_width_cache" ] || terminal_width_cache="$(tput cols 2>/dev/null || true)"
        case "$terminal_width_cache" in
            ''|*[!0-9]*) terminal_width_cache=80 ;;
        esac
        [ "$terminal_width_cache" -ge 40 ] || terminal_width_cache=80
        [ "$terminal_width_cache" -le 100 ] || terminal_width_cache=100
    fi
    echo "$terminal_width_cache"
}

# Prose, wrapped where it is printed rather than where it is written.
#
# Every message used to be hand-wrapped to 80 columns, which was wrong twice over: it assumed the
# reader's terminal, and it made the LINE the unit instead of the paragraph. That second one matters
# for translation - a translated sentence is a different length, so line breaks decided in the source
# cannot survive it - and it is why this exists now rather than when the catalogue arrives.
#
# Leading spaces in the text are the paragraph's indent and are kept on every wrapped line, so the
# indented explanations under a question stay indented.
say() {
    if [ $# -eq 0 ]; then
        echo >&2
        return 0
    fi

    local text="$*" indent width
    indent="${text%%[! ]*}"
    text="${text#"$indent"}"

    width=$(( $(terminal_width) - ${#indent} ))
    [ "$width" -ge 20 ] || width=20

    # fold -s breaks at spaces and leaves them at the break, hence the trim. Both are POSIX and
    # present everywhere this runs, including the container and trixie-minbase.
    printf '%s\n' "$text" | fold -s -w "$width" | sed "s/^/$indent/; s/[[:space:]]*\$//" >&2
}

# For things that must NOT be re-flowed: a path, or anything already laid out in columns. fold has
# no word to break on in /a/very/long/path, so it breaks mid-path - and a path split across lines
# cannot be read or selected. Letting the terminal soft-wrap it is the lesser evil.
say_raw() {
    printf '%s\n' "$*" >&2
}

warn() {
    local width
    width=$(( $(terminal_width) - 4 ))
    [ "$width" -ge 20 ] || width=20
    printf '%s\n' "$*" | fold -s -w "$width" | sed 's/^/  ! /; s/[[:space:]]*$//' >&2
}

# ------------------------------------------------------------------------------------------------
# The questions
# ------------------------------------------------------------------------------------------------

ask_printer_host() {
    say
    say $"The address printers use to reach this server."
    say
    say $"  This is the one setting with no sensible default, and the one that is expensive to get wrong: it is written into every USB provisioning bundle, and the printer certificate is issued once - on the first start - covering this address and every address the machine can see. Set it now and it is covered by construction."
    say

    local current candidates choice n line addr iface name_line
    current="$(env_get PRINTER_HOST)"
    candidates="$(lan_addresses)"

    # After the addresses, because the address is the safe answer and the name is the durable one -
    # and somebody who wants the name will read the whole list anyway.
    name_line="$(name_candidate "$candidates")"
    [ -n "$name_line" ] && candidates="$candidates
$name_line"

    if [ -n "$candidates" ]; then
        say $"  Addresses on this machine that a printer could reach:"
        n=0
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            n=$((n + 1))
            # The interface name is what makes this choosable when more than one survives the
            # filtering - Ethernet against Wi-Fi looks identical as two RFC1918 addresses, and a
            # rule cannot pick between them because both are genuinely reachable.
            addr="${line%%	*}"
            iface="${line#*	}"
            if [ "$iface" = "$line" ]; then
                say $"    $n) $addr"
            else
                say "$(printf '    %d) %-16s %s' "$n" "$addr" "$iface")"
            fi
        done <<< "$candidates"
        say "    $((n + 1))) something else - a name, or an address not listed"
        if [ -n "$name_line" ]; then
            say
            say $"  A name keeps working when this machine's address changes - an address does not - but only while your router keeps publishing it, which is not checked here. Test it from another machine before relying on it."
        fi
        say

        choice="$(ask "  Which" "${current:-1}")"
        # A bare number picks from the list; anything else is taken literally, so an operator who
        # already knows their hostname can simply type it rather than working out which option means
        # "let me type it".
        case "$choice" in
            ''|*[!0-9]*) : ;;
            *)
                if [ "$choice" -ge 1 ] && [ "$choice" -le "$n" ]; then
                    # The address only - the interface name was for reading, not for writing.
                    choice="$(echo "$candidates" | sed -n "${choice}p")"
                    choice="${choice%%	*}"
                elif [ "$choice" -eq $((n + 1)) ]; then
                    choice="$(ask "  Address or name")"
                fi
                ;;
        esac
    else
        warn $"No usable address detected on this machine. Loopback, link-local and Docker's own ranges are excluded - a printer cannot reach any of them, and one baked into the certificate cannot be corrected without a reissue."
        choice="$(ask "  Address or name" "$current")"
    fi

    validate_printer_host "$choice" || return 0
    plan_set PRINTER_HOST "$choice"
}

validate_printer_host() {
    local host="$1" resolved hit

    if [ -z "$host" ]; then
        warn $"Left unset. USB-key provisioning will refuse to produce a snippet until it is."
        return 1
    fi

    case "$host" in
        localhost|127.*|0.0.0.0)
            warn $"$host is this machine talking to itself - no printer can reach it."
            ask_yes_no $"  Use it anyway" n || return 1
            ;;
    esac

    # .local is USUALLY mDNS, and mDNS is a name a printer can never use: Buddy broadcasts its own
    # presence that way but cannot resolve it, so such a name works from every machine the operator
    # will test on and nowhere it has to work.
    #
    # But not always. RFC 6762 only reserved .local in 2013, and networks built before it - Windows
    # SBS domains especially - legitimately serve .local from ordinary unicast DNS, where a printer
    # can resolve it perfectly. So this asks rather than assumes, and the question is answerable:
    # dig, nslookup and host query the configured DNS servers and do NOT do mDNS. Verified rather
    # than believed - on a normal LAN, dig returns nothing for <host>.local and the address for
    # <host>.lan.
    case "$host" in
        *.local)
            resolves_in_dns "$host"
            case $? in
                0)
                    say $"  $host is served by ordinary DNS, not mDNS - a printer can resolve it."
                    ;;
                2)
                    warn $"$host is probably an mDNS name, and Prusa firmware announces itself over mDNS but cannot resolve it - so a printer would never find this server, however well the name works from your own machine. There is no dig, nslookup or host here to check whether your network serves .local from real DNS instead, which some older ones do."
                    ask_yes_no $"  Use it anyway" n || return 1
                    ;;
                *)
                    warn $"$host is an mDNS name - it does not resolve in DNS, only by multicast. Prusa firmware announces itself over mDNS but cannot resolve it, so a printer given this name will never find this server, even though it resolves perfectly from your own machine. Use the address, or a name your router publishes in DNS."
                    ask_yes_no $"  Use it anyway" n || return 1
                    ;;
            esac
            ;;
    esac

    # A name rather than an address: resolve it, and say what the certificate will end up covering.
    # A stale DNS entry is invisible until a printer fails to verify, and by then the certificate is
    # already issued.
    case "$host" in
        *[!0-9.]*)
            resolved="$(resolve_host "$host")"
            if [ -z "$resolved" ]; then
                warn $"$host does not resolve from here. The certificate will cover the name, but a printer that cannot resolve it either will have nothing to connect to."
                ask_yes_no $"  Use it anyway" y || return 1
            else
                say $"  $host resolves to $resolved - the certificate will cover both."
            fi
            ;;
    esac

    # The same ranges the offered list was filtered against, so a typed address is judged by the
    # rule a picked one already passed.
    hit="$(overlaps_any "${resolved:-$host}/32" <<< "$(unreachable_ranges)" | head -1)"
    if [ -n "$hit" ]; then
        warn $"$host is inside $hit, which printers on your network cannot route to. It would be minted into the certificate and frozen there."
        ask_yes_no $"  Use it anyway" n || return 1
    fi

    return 0
}

# Whether a name resolves in UNICAST DNS, as distinct from mDNS. 0 yes, 1 no, 2 nothing here can ask.
#
# resolve_host cannot answer this: getent and dscacheutil both go through the unified resolver, which
# includes mDNS, so they say yes to a .local name that only multicast knows. dig, nslookup and host
# talk to the configured DNS servers and nothing else, which is exactly the distinction wanted.
#
# The unknown case warns rather than blessing, and deliberately: wrongly approving an mDNS name costs
# a PRINTER_HOST no printer can reach, frozen into a certificate, while wrongly warning costs one
# keystroke. Containers land here - none of these tools is in a base image.
resolves_in_dns() {
    local name="$1"
    if command -v dig >/dev/null 2>&1; then
        [ -n "$(dig +short +time=2 +tries=1 "$name" A 2>/dev/null | grep -E '^[0-9]+\.')" ]
    elif command -v nslookup >/dev/null 2>&1; then
        nslookup -type=A "$name" 2>/dev/null | grep -qE '^Address: [0-9]+\.'
    elif command -v host >/dev/null 2>&1; then
        host -t A "$name" >/dev/null 2>&1
    else
        return 2
    fi
}

resolve_host() {
    if command -v getent >/dev/null 2>&1; then
        # IPv4 ONLY, and this is the whole point of ahostsv4. `getent hosts` returns AAAA first on a
        # network with IPv6, so on the Pi this answered homespool.lan with fdc2:74d8:1010::cd4 - a
        # correct address, compared against a candidate list that is IPv4 by construction, matching
        # nothing, and silently dropping the one name that worked.
        #
        # Everything downstream is IPv4: the addresses come from `ip -4`, the certificate covers what
        # a printer dials over v4, and the CIDR arithmetic here is 32-bit. Asking for A records is
        # not a limitation, it is the question being asked.
        # Tested for output, not for exit status: awk succeeds having printed nothing, so `&&` here
        # would report success on an empty answer and the fallback would never run.
        local resolved
        resolved="$(getent ahostsv4 "$1" 2>/dev/null | awk 'NR == 1 { print $1 }')"

        # ahostsv4 is glibc's; musl has getent without it. Filtering hosts output is the fallback.
        [ -n "$resolved" ] || resolved="$(getent hosts "$1" 2>/dev/null \
            | awk '{ print $1 }' | grep -E '^[0-9]+\.' | head -1)"
        echo "$resolved"
    else
        # macOS has no getent. dscacheutil goes through the same resolver the OS uses, so it agrees
        # with /etc/hosts and mDNS where a bare DNS query would not.
        dscacheutil -q host -a name "$1" 2>/dev/null | awk '/^ip_address:/ { print $2; exit }'
    fi
}

ask_user_host() {
    say
    local suggestion
    suggestion="$(suggested_user_host)"
    say $"The name people type in a browser. Cosmetic - it names the self-signed certificate, and a browser warns about that certificate whatever name it carries."
    plan_set USER_HOST "$(ask "  Name" "$suggestion")"
}

ask_timezone() {
    say
    say $"The zone timestamps are shown in. The conversion happens on the server, so this decides what print history reads and what an invitation email says its expiry is."
    plan_set TZ "$(ask "  Timezone" "$(prefer_current TZ "$(detect_timezone)")")"
}

# This machine's own name, mDNS-qualified, which is the answer on a board handed its address by
# DHCP: the lease can move and the name still resolves. Anything already chosen wins over it.
suggested_user_host() {
    local current suggestion
    current="$(env_get USER_HOST)"
    if [ -n "$current" ] && [ "$current" != localhost ]; then
        echo "$current"
        return 0
    fi
    suggestion="$(qualified_machine_name)"
    echo "${suggestion:-localhost}"
}

# This machine's name, or nothing when there is no honest answer.
#
# INSIDE A CONTAINER THERE IS NO HONEST ANSWER. `hostname` returns the container id, so the Windows
# path - where the wizard runs in a one-off `docker run` - suggested "5d44b2605478.local" as the name
# to type into a browser. It is meaningless the moment the container exits.
#
# HOMESPOOL_HOSTNAME is how setup-env.ps1 supplies the real one, the same way it supplies addresses:
# a fact from outside that the inside cannot obtain. WSL needs nothing, because WSL2 takes its
# hostname from the Windows machine already.
machine_name() {
    if [ -n "${HOMESPOOL_HOSTNAME:-}" ]; then
        echo "$HOMESPOOL_HOSTNAME"
        return 0
    fi
    in_container && return 0
    hostname 2>/dev/null || true
}

# The names this machine might answer to, best first, one per line.
#
# A bare hostname was qualified to .local and nothing else, which is wrong wherever the network has a
# domain of its own: a Pi called "homespool" on a router publishing .lan answers to homespool.lan,
# and .local is the one form that machine cannot even check - trixie-minbase has no dig, nslookup or
# host, so it gets treated as mDNS and dropped. The name that works was never offered.
#
# So: the FQDN if the resolver knows one, then the short name against each search domain from
# resolv.conf, and .local last as the fallback it always was. The caller offers the first that
# actually resolves to an address on the list, so a wrong guess here costs nothing.
# Deduplicated, because the sources overlap by design: reverse DNS and the hostname-plus-domain
# guess usually agree, and agreeing twice is not two candidates.
candidate_names() {
    candidate_names_raw "${1:-}" | awk 'NF && !seen[$0]++'
}

candidate_names_raw() {
    local addresses="${1:-}" name fqdn domain address

    # Supplied from outside wins outright, ahead of every lookup. setup-env.ps1 read this off the
    # Windows host; nothing discovered from inside a container knows better than that, and a reverse
    # lookup that happens to answer would quietly overrule it.
    if [ -n "${HOMESPOOL_HOSTNAME:-}" ]; then
        echo "$HOMESPOOL_HOSTNAME"
        return 0
    fi

    # REVERSE DNS FIRST, because it is the only source that asks the network what it calls this
    # machine rather than assembling a name and hoping. It is also the only one that works on the
    # board this was written for: the Pi's resolv.conf says "search ." - the router publishes the
    # name homespool.lan without publishing the suffix - so every hostname-plus-domain guess comes up
    # empty while a reverse lookup answers immediately.
    while IFS= read -r address; do
        address="${address%%	*}"
        [ -n "$address" ] || continue
        reverse_name "$address"
    done <<< "$addresses"

    name="$(machine_name)"
    [ -n "$name" ] || return 0

    case "$name" in
        *.*) echo "$name"; return 0 ;;
    esac

    fqdn="$(hostname -f 2>/dev/null || true)"
    case "$fqdn" in
        *.*) echo "$fqdn" ;;
    esac

    for domain in $(search_domains); do
        case "$domain" in
            .|localdomain) continue ;;
            *) echo "$name.$domain" ;;
        esac
    done

    echo "$name.local"
}

# What the network calls an address, or nothing. The trailing dot on a fully-qualified answer is
# stripped: it is correct DNS notation and wrong everywhere it would then be pasted.
reverse_name() {
    local address="$1" name=""
    if command -v getent >/dev/null 2>&1; then
        name="$(getent hosts "$address" 2>/dev/null | awk 'NR == 1 { print $2 }')"
    elif command -v dig >/dev/null 2>&1; then
        name="$(dig +short +time=2 +tries=1 -x "$address" 2>/dev/null | head -1)"
    elif command -v host >/dev/null 2>&1; then
        name="$(host "$address" 2>/dev/null | awk '/domain name pointer/ { print $NF; exit }')"
    elif command -v dscacheutil >/dev/null 2>&1; then
        name="$(dscacheutil -q host -a ip_address "$address" 2>/dev/null | awk '/^name:/ { print $2; exit }')"
    fi
    [ -n "$name" ] && echo "${name%.}"
}

# The network's own domains, from whichever of the two places is telling the truth here.
#
# resolvectl first, because on a systemd-resolved machine /etc/resolv.conf is a symlink to a stub and
# the answer is really the daemon's - `resolvectl domain` asks it directly and needs no guess about
# which of stub-resolv.conf and resolv.conf is in play. Falling back to the file for everything else,
# which is still most things.
search_domains() {
    if command -v resolvectl >/dev/null 2>&1; then
        resolvectl domain 2>/dev/null \
            | sed -n 's/.*: *//p' \
            | tr ' ' '\n' \
            | sed 's/^~//' \
            | grep -vE '^$' && return 0
    fi

    [ -r /etc/resolv.conf ] || return 0
    awk '/^[[:space:]]*(search|domain)[[:space:]]/ { for (i = 2; i <= NF; i++) print $i }' \
        /etc/resolv.conf 2>/dev/null
}

# The best single name, for USER_HOST - which a browser resolves, so .local is fine there.
# The addresses ARE passed, so reverse DNS gets a say here too.
#
# Withholding them was a mistake with a visible result: PRINTER_HOST was offered homespool.lan while
# USER_HOST suggested homespool.local, two names for one machine on one screen. The reason given was
# that a browser resolves mDNS perfectly well - true, and not a reason to prefer it. If the network
# publishes a real name, that is the name, and one the operator can also use for the printers.
#
# .local remains the fallback, and only here: a browser can resolve it and the firmware cannot, which
# is why PRINTER_HOST still refuses one that unicast DNS does not serve.
qualified_machine_name() {
    # Addresses are passed only when they describe THIS machine. Inside a container they are the
    # container's own, and a reverse lookup of those names the container - which is the trap the
    # container guard exists to avoid, arriving by a different route. With HOMESPOOL_ADDRESSES set
    # they came from the host, so they are worth asking about.
    if in_container && [ -z "${HOMESPOOL_ADDRESSES:-}" ]; then
        candidate_names "" | head -1
    else
        candidate_names "$(lan_addresses)" | head -1
    fi
}

# This machine's name as a candidate for PRINTER_HOST, offered only when it resolves to an address
# already on the list.
#
# A name is the more durable answer - an address stops working the moment the DHCP lease moves, and
# PrinterAddressSuggestion says exactly that about it: "survives a change of address, but only if
# your router publishes names to its own DNS". The resolve check is what tests that proviso rather
# than assuming it, so a name that goes nowhere is never suggested.
name_candidate() {
    local addresses="$1" name resolved
    while IFS= read -r name; do
        [ -n "$name" ] || continue
        name_candidate_one "$addresses" "$name" && return 0
    done <<< "$(candidate_names "$addresses")"
    return 0
}

# One name, offered or not. Split out so the loop above reads as "the first of these that works".
name_candidate_one() {
    local addresses="$1" name="$2" resolved

    # A .local name only survives if UNICAST DNS serves it. Buddy broadcasts its presence over mDNS
    # but cannot resolve it, so an mDNS-only name is one a printer can never reach - however well it
    # works from a browser, which is the trap. USER_HOST keeps .local for exactly the opposite
    # reason: the thing resolving it there is a desktop, which does mDNS perfectly well.
    #
    # Not a blanket exclusion, because .local was only reserved for mDNS in 2013 and networks built
    # before that legitimately serve it from real DNS. Withholding a name that would work is its own
    # kind of wrong; the unknown case is treated as mDNS, which is the safe direction.
    case "$name" in
        *.local) resolves_in_dns "$name" || return 0 ;;
    esac

    resolved="$(resolve_host "$name")"
    [ -n "$resolved" ] || return 0

    # It has to point at an address this machine would otherwise have offered. A name resolving
    # somewhere else entirely is a stale DNS entry, and baking that into a certificate is the
    # failure this whole question exists to avoid.
    echo "$addresses" | awk -F'\t' -v want="$resolved" '$1 == want { found = 1 } END { exit found ? 0 : 1 }' \
        || return 0

    # States what was actually checked - that it resolves here, and to what - and nothing more.
    # "survives a new DHCP lease" was the first wording and it overpromised: this resolver includes
    # mDNS and answers only for right now, which says nothing about whether the router will keep
    # publishing the name, or whether a PRINTER can resolve it at all. The caveat that matters is
    # printed under the list rather than squeezed into the line.
    printf '%s\ta name - resolves here to %s\n' "$name" "$resolved"
}

# An existing setting beats a detected one: somebody who has already chosen is not asking to be
# corrected by a machine that has just noticed where it is.
prefer_current() {
    local key="$1" detected="$2" current
    current="$(env_get "$key")"
    if key_present "$env_file" "$key" && [ -n "$current" ] && [ "$current" != UTC ]; then
        echo "$current"
    else
        echo "$detected"
    fi
}

ask_ports() {
    say
    if ask_yes_no "Are ports 80 and 443 free on this machine" y; then
        return 0
    fi

    local http https suffix
    http="$(ask "  HTTP port" "$(env_get PORT)")"
    https="$(ask "  HTTPS port" "$(env_get HTTPS_PORT)")"
    plan_set PORT "$http"
    plan_set HTTPS_PORT "$https"

    # Derived and then confirmed rather than set silently, because the two are not the same fact.
    # HTTPS_PORT is where Docker publishes on THIS machine; the suffix is the port a browser should
    # ask for, and they differ the moment a router forwards 443 inward or a tunnel sits in front.
    if [ "$https" = 443 ]; then
        suffix=""
    else
        suffix=":$https"
    fi
    say
    say $"  Plain-HTTP visitors are redirected to HTTPS, and the redirect has to name the port a BROWSER should ask for - which is not necessarily the one published here, if anything forwards to this machine."
    plan_set HTTPS_PORT_SUFFIX "$(ask "  Port suffix in the redirect" "$suffix")"
}

ask_smtp() {
    say
    say $"Outgoing mail is optional. Without it, new accounts are created already confirmed and password reset is unavailable - invitations still work, you just pass the link on yourself."
    say

    local current_host
    current_host="$(env_get SMTP_HOST)"
    if ! ask_yes_no "Configure outgoing mail" "$([ -n "$current_host" ] && echo y || echo n)"; then
        # Only clears a host that is actually set, so answering "no" on a stack that never had mail
        # is not a change to anything.
        [ -n "$current_host" ] && ask_yes_no "  Turn off the mail you have configured" n \
            && plan_set SMTP_HOST ""
        return 0
    fi

    local host
    host="$(ask "  Mail server" "$current_host")"

    # The same mistake PRINTER_HOST is checked for, and for the same reason: this runs in a
    # container, so localhost is the container. A mail server on the machine is refused from in
    # there, and the error says "connection refused" about a server that is plainly running.
    case "$host" in
        localhost|127.0.0.1|::1)
            warn $"Homespool runs in a container, so $host is the container itself - not this machine."
            if ask_yes_no "  Use host.docker.internal, which reaches the host" y; then
                host="host.docker.internal"
            fi
            ;;
    esac
    plan_set SMTP_HOST "$host"

    # Port and encryption are one decision on the three well-known ports, so one question settles
    # both - asked separately they can be made to disagree, and each half then fails in a way that
    # reads as a broken server rather than a wrong setting.
    local port implicit disable
    port="$(ask "  Port - 587 for STARTTLS, 465 for implicit TLS, 25 for none" "$(env_get SMTP_PORT)")"
    case "$port" in
        465) implicit=true;  disable=false ;;
        587) implicit=false; disable=false ;;
        25)  implicit=false; disable=true  ;;
        *)
            # Any other port says nothing about encryption, and guessing STARTTLS is how a local
            # Mailpit on 1025 fails at send time with "does not support the STARTTLS extension" -
            # long after this question, when the connection is the last thing anyone suspects.
            say
            say $"  Port $port is not one of the three well-known ones, so it does not say how the connection is encrypted. It has to match what the server offers."
            say $"    1) STARTTLS - upgraded after connecting 2) implicit TLS - encrypted from the first byte 3) none - only for a server on this machine; sends the password in the clear"
            case "$(ask "  Which" 1)" in
                2) implicit=true;  disable=false ;;
                3) implicit=false; disable=true  ;;
                *) implicit=false; disable=false ;;
            esac
            ;;
    esac
    plan_set SMTP_PORT "$port"
    plan_set SMTP_USE_IMPLICIT_TLS "$implicit"
    plan_set SMTP_DISABLE_TLS "$disable"

    local username
    username="$(ask "  Username (empty connects without authenticating)" "$(env_get SMTP_USERNAME)")"
    plan_set SMTP_USERNAME "$username"
    if [ -n "$username" ]; then
        local password
        password="$(ask_secret "  Password (empty keeps the current one)")"
        [ -n "$password" ] && plan_set SMTP_PASSWORD "$password"
    fi

    plan_set SMTP_FROM_ADDRESS "$(ask "  From address (empty uses the username)" "$(env_get SMTP_FROM_ADDRESS)")"
    plan_set SMTP_FROM_NAME "$(ask "  From name" "$(env_get SMTP_FROM_NAME)")"
}

# ------------------------------------------------------------------------------------------------
# The checks nobody is asked about
# ------------------------------------------------------------------------------------------------

# go2rtc has one credential for its whole API and no notion of which cameras a caller may see, so
# this is defence in depth rather than the access control - Homespool still proxies every viewing
# path and applies the camera's own permission check. It costs nothing and there is no decision in
# it, so there is no question either: generate one if there is none, and never touch an existing one.
ensure_go2rtc_credential() {
    local password
    password="$(env_get GO2RTC_PASSWORD)"
    [ -n "$password" ] && return 0

    password="$(random_password)"
    if [ -z "$password" ]; then
        warn $"No source of randomness found - leaving the camera sidecar unauthenticated, which is what this deployment already had. Its port is not published."
        return 0
    fi

    # Both or neither, always. A username with an empty password switches the sidecar's
    # authentication on with an empty key and locks Homespool out along with everyone else.
    plan_set GO2RTC_USERNAME homespool
    plan_set GO2RTC_PASSWORD "$password"
    say $"Generated a credential for the camera sidecar."
}

random_password() {
    # Base64 of 24 random bytes. Its alphabet has no quote, backslash or dollar, so the result is
    # safe unquoted in .env, through compose's own ${...} interpolation, and inside the JSON the
    # go2rtc service is started with.
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 24
    elif [ -r /dev/urandom ]; then
        head -c 24 /dev/urandom | base64
    fi
}

# ------------------------------------------------------------------------------------------------
# Answering without being asked
#
# What a machine can work out about itself, taken as the answer. With --no-overwrite this fills in
# blanks and touches nothing else, which is what makes it safe for a systemd unit to run on every
# boot with no "have I done this before" stamp file.
# ------------------------------------------------------------------------------------------------
auto_answer() {
    local address creating=false
    [ -f "$env_file" ] || creating=true

    # THIS MUST BE FATAL, and the reason is a bug this project shipped. The unit is
    # RemainAfterExit=yes, so a run that exits 0 having done nothing is recorded as *Finished,
    # successfully* and never retried - a board sat with a working network and no stack until it was
    # power-cycled, because the ethernet cable went in five minutes after the one attempt. Failing
    # is what lets Restart=on-failure turn that into the self-healing case.
    # First field: auto_answer wants the address, not the label beside it.
    address="$(lan_addresses | head -1)"
    address="${address%%	*}"
    if [ -z "$address" ]; then
        echo "setup-env.sh: no address found that a printer could reach - not writing anything." >&2
        echo "setup-env.sh: this is the normal state before the network is up. Exiting non-zero so" >&2
        echo "setup-env.sh: a supervisor retries rather than recording a success." >&2
        exit 1
    fi

    say $"Detected $address as the address printers reach this server on."
    plan_set PRINTER_HOST "$address"
    plan_set USER_HOST "$(suggested_user_host)"
    plan_set TZ "$(detect_timezone)"
    ensure_go2rtc_credential

    # Only while creating the file. Moving the compose network under a stack that is already running
    # is not something to do unattended, and after the first write the answer is somebody's - even if
    # it was this function's.
    $creating && auto_move_subnet
    return 0
}

# The unattended half of check_subnet_collision: same question, but nobody to ask, so it takes the
# first free range and says loudly which one and why.
auto_move_subnet() {
    local subnet colliding candidate
    subnet="$(env_get PROXY_SUBNET)"
    [ -n "$subnet" ] || return 0

    colliding="$(overlaps_any "$subnet" <<< "$(allocated_ranges)")" || true
    [ -n "$colliding" ] || return 0

    candidate="$(free_subnet)"
    if [ -z "$candidate" ]; then
        warn "The compose network $subnet collides with $(echo "$colliding" | tr '\n' ' ')and every"
        warn $"/16 from 172.16 to 172.31 is taken. Set PROXY_SUBNET and PROXY_NETWORK by hand."
        return 0
    fi

    say "The compose network $subnet collides with $(echo "$colliding" | tr '\n' ' ')- using"
    say $"$candidate instead."
    plan_set PROXY_SUBNET "$candidate"
    plan_set PROXY_NETWORK "$candidate"
}

# The subnet is not a question - it is right until it collides with something, and the operator has
# no way of knowing that in advance. So it is checked, and only mentioned when it is wrong.
check_subnet_collision() {
    local subnet colliding candidate
    subnet="$(env_get PROXY_SUBNET)"
    [ -n "$subnet" ] || return 0

    # Emptiness is the test, not the exit status - and the `|| true` is load-bearing under `set -e`:
    # overlaps_any reports "no overlap" by failing, which is the ORDINARY case here, and a bare
    # assignment from a failing substitution ends the script. It did, silently, right after the
    # camera credential and before anything was written.
    colliding="$(overlaps_any "$subnet" <<< "$(allocated_ranges)")" || true
    [ -n "$colliding" ] || return 0
    colliding="$(echo "$colliding" | tr '\n' ' ' | sed 's/ *$//; s/^/ /')"

    say
    warn $"The compose network $subnet collides with:$colliding"
    say
    warn $"A collision with another Docker network fails loudly at startup. A collision with a route this machine already has does not: the stack comes up, and that network stops being reachable from here."

    candidate="$(free_subnet)"
    if [ -z "$candidate" ]; then
        warn $"Every /16 from 172.16 to 172.31 is in use here - pick a range by hand."
        return 0
    fi

    if ask_yes_no "  Move the compose network to $candidate" y; then
        # Both, always. One is what Docker allocates and the other is what the application trusts
        # for forwarded headers and treats as container-only; they answer different questions from
        # the same fact, and a stack where they disagree believes headers from the wrong network.
        plan_set PROXY_SUBNET "$candidate"
        plan_set PROXY_NETWORK "$candidate"
    fi
}

free_subnet() {
    local taken octet candidate
    taken="$(allocated_ranges)"
    for octet in $(seq 16 31); do
        candidate="172.$octet.0.0/16"
        if ! overlaps_any "$candidate" <<< "$taken" >/dev/null; then
            echo "$candidate"
            return 0
        fi
    done
}

# PRINTER_HOST's whole urgency is "before the first start", so an operator changing it afterwards is
# owed the rest of the sentence rather than being left to discover the certificate never moved.
warn_if_already_started() {
    case "$pending" in
        *PRINTER_HOST=*) : ;;
        *) return 0 ;;
    esac
    command -v docker >/dev/null 2>&1 || return 0

    # Scoped to THIS compose project, not to any volume with the right name. The same machine can
    # hold a data volume from a checkout elsewhere, and telling somebody to reissue a certificate
    # that was never issued here sends them looking for a problem they do not have.
    local project
    project="${COMPOSE_PROJECT_NAME:-$(basename "$repo_root" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9_-')}"
    [ -n "$(docker volume ls -q \
                --filter label=com.docker.compose.project="$project" \
                --filter label=com.docker.compose.volume=homespool-data 2>/dev/null)" ] || return 0

    say
    warn $"This stack has run before, so the printer certificate has already been issued - and it is issued once. Changing PRINTER_HOST in .env does not change what that certificate covers. Reissue it afterwards from Admin -> Printer certificate, or printers will fail to verify."
}

# ------------------------------------------------------------------------------------------------
# Writing
# ------------------------------------------------------------------------------------------------

is_secret() {
    case " $secret_keys " in
        *" $1 "*) return 0 ;;
    esac
    return 1
}

display_value() {
    local key="$1" value="$2"
    if [ -z "$value" ]; then
        echo "(empty)"
    elif is_secret "$key"; then
        echo "(set, ${#value} characters)"
    else
        echo "$value"
    fi
}

summarise() {
    local key value before after line key_width

    # Padded to the longest key actually being changed rather than a fixed 24, so a narrow terminal
    # is not made narrower by columns nothing occupies.
    key_width=0
    while IFS= read -r line; do
        [ -n "$line" ] || continue
        key="${line%%=*}"
        [ "${#key}" -le "$key_width" ] || key_width="${#key}"
    done <<< "$pending"
    [ "$key_width" -ge 8 ] || key_width=8
    say
    say $"This will change:"
    say_raw "    $env_file"
    say
    while IFS= read -r line; do
        [ -n "$line" ] || continue
        key="${line%%=*}"
        value="${line#*=}"
        before="$(env_get "$key")"
        if ! key_present "$env_file" "$key"; then
            # The "default" clause only when there is a default worth naming. Otherwise this said
            # "(unset, default (empty))" - two brackets to report that a setting with no value has
            # no value, on the line somebody reads before answering yes.
            if [ -z "$before" ]; then
                before="(unset)"
            else
                before="(unset, default $(display_value "$key" "$before"))"
            fi
        else
            before="$(display_value "$key" "$before")"
        fi
        # A table, so it is NOT word-wrapped - breaking a value across lines would make the one
        # screen that has to be read carefully the hardest to read. Instead the columns adapt, and
        # an entry that still will not fit is split over two lines rather than overflowing.
        after="$(display_value "$key" "$value")"
        line="$(printf '    %-*s %s  ->  %s' "$key_width" "$key" "$before" "$after")"
        if [ "${#line}" -le "$(terminal_width)" ]; then
            printf '%s\n' "$line" >&2
        else
            printf '    %s\n' "$key" >&2
            printf '        %s  ->  %s\n' "$before" "$after" >&2
        fi
    done <<< "$pending"
    say
    say $"Every other line - comments, blank lines, and any key not listed - is left as it is."
}

apply() {
    # Seeded from the example rather than written from nothing, so that somebody who opens this file
    # later still finds the documentation for the twenty-odd settings the wizard never asked about.
    if [ ! -f "$env_file" ]; then
        cp "$example_file" "$env_file"
        say $"Created $env_file from .env.example."
    fi

    # A copy before anything is written, because .env is the one file here with nothing behind it:
    # it is gitignored, so there is no commit to go back to, and it holds SMTP_PASSWORD and
    # GO2RTC_PASSWORD. Every other mistake in this repository is recoverable and this one is not.
    #
    # Timestamped rather than a single .env.bak so a second run does not eat the first backup - which
    # is exactly when somebody is fixing a wrong answer and most wants the one before it. Taken on
    # every run that gets this far, including one that changes nothing: a spare copy is litter, and
    # litter is cheaper than the alternative.
    #
    # Created with the mode already set rather than chmod'ed afterwards, so the secrets are never on
    # disk world-readable, even briefly.
    if [ -f "$env_file" ]; then
        local backup
        # date(1) rather than printf's %(...)T, which would be tidier and is a trap: that format is
        # bash 4.2+, and macOS still ships bash 3.2. Nothing else in this file needs bash 4, which is
        # what lets it run on macOS, WSL and Linux alike - so this would have been the single
        # construct that broke one of the three, on the platform least likely to be tested first.
        # +%Y%m%d-%H%M%S is POSIX, so GNU and BSD date both take it.
        #
        # The fallback is not decoration. Under `set -e` an assignment whose command substitution
        # fails ends the script THERE, silently - so a missing date(1) aborted the run between
        # seeding .env and patching it, leaving the file created and unwritten with no message
        # beyond "command not found". The backup is a nicety; it must never be the thing that loses
        # somebody's answers. $$ is unique enough for a file nobody sorts.
        backup="$env_file.backup-$(date +%Y%m%d-%H%M%S 2>/dev/null || echo "$$")"
        ( umask 077 && cat "$env_file" > "$backup" ) \
            && say "Kept a copy of your previous settings at $(basename "$backup")."
    fi

    # The pending pairs, escaped, in a file of their own so awk can read them as its first input.
    # One pass over .env rather than one pass per key: the old shape rewrote the whole file once for
    # every answer, which is six rewrites of a 190-line file to change six lines.
    local pairs tmp key value
    pairs="$(mktemp "${TMPDIR:-/tmp}/setup-env-pairs.XXXXXX")"
    tmp="$(mktemp "${TMPDIR:-/tmp}/setup-env.XXXXXX")"
    while IFS= read -r line; do
        [ -n "$line" ] || continue
        key="${line%%=*}"
        value="${line#*=}"
        printf '%s=%s\n' "$key" "$(compose_escape "$value")" >> "$pairs"
    done <<< "$pending"

    # Values are carried in the pairs file rather than interpolated into the program text, and keys
    # are matched with index() rather than a regex, so a password containing a backslash, an
    # ampersand or a slash lands in the file exactly as typed.
    #
    # The LAST assignment of a key is the one rewritten, which is what makes writing agree with
    # reading: file_get also takes the last, on the same reasoning a shell and compose use - an
    # operator who has appended a second PRINTER_HOST= at the bottom means the bottom one. Rewriting
    # the first would have shown them one value in the summary and changed a different line.
    awk '
        FNR == NR {
            eq = index($0, "=")
            key = substr($0, 1, eq - 1)
            value[key] = substr($0, eq + 1)
            order[++count] = key
            next
        }
        { line[++total] = $0 }
        END {
            for (i = 1; i <= total; i++) {
                for (k in value) {
                    if (index(line[i], k "=") == 1) {
                        last[k] = i
                    }
                }
            }
            for (i = 1; i <= total; i++) {
                replaced = ""
                for (k in value) {
                    # `k in last` first, and not merely for speed: referencing last[k] CREATES it,
                    # so a bare `last[k] == i` would quietly populate last with every key in value -
                    # and the append below, which asks whether a key is in last, would then never
                    # fire. A key the file does not mention would be silently dropped.
                    if ((k in last) && last[k] == i) {
                        replaced = k
                    }
                }
                # Parenthesised because `print` takes an expression *list* and treats a bare `>` as
                # redirection: gawk tolerates an unbracketed ternary here, BSD awk rejects the whole
                # program. macOS ships BSD awk.
                print (replaced == "" ? line[i] : replaced "=" value[replaced])
            }
            # Anything the file never mentioned is appended, in the order it was answered.
            for (i = 1; i <= count; i++) {
                if (!(order[i] in last)) {
                    print order[i] "=" value[order[i]]
                }
            }
        }
    ' "$pairs" "$env_file" > "$tmp"

    # Copied over rather than moved, so an existing file keeps its own ownership and mode.
    cat "$tmp" > "$env_file"
    rm -f "$tmp" "$pairs"

    # A file holding an SMTP password should not be world-readable.
    chmod 600 "$env_file" 2>/dev/null || true
}

# ------------------------------------------------------------------------------------------------
# Entry
#
# Everything above is functions with no side effects at load time, and everything that acts is below
# - so `source setup-env.sh` gets the whole toolbox and runs none of it. That is what tests/ drives:
# it calls the parsing and the patching directly, with fake `docker`, `ip` and `netstat` ahead of
# them on PATH, which is the only way the BSD route parser gets exercised on Linux and the Linux one
# on a Mac. Keep new work above the line, and keep this block a call to main.
# ------------------------------------------------------------------------------------------------

main() {

    while [ $# -gt 0 ]; do
        case "$1" in
            --set)
                case "${2:-}" in
                    # Collected, not applied. plan_set consults --no-overwrite, so acting here would
                    # make the answer depend on the order the flags were typed in:
                    # `--set X=1 --no-overwrite` would overwrite and `--no-overwrite --set X=1`
                    # would not. Every flag is read before anything is decided.
                    *=*) set_args="$set_args$2
"; non_interactive=true; shift 2 ;;
                    *) echo "setup-env.sh: --set wants KEY=VALUE" >&2; exit 2 ;;
                esac
                ;;
            --no-prompt) no_prompt=true; shift ;;
            --no-overwrite) no_overwrite=true; shift ;;
            --dry-run) dry_run=true; shift ;;
            # 2,16 rather than a fixed larger range: the usage block ends at the DHCP warning, and a
            # range past it prints half a paragraph about compose defaults while one short of it cuts
            # a sentence in half. Extend when the header does - and check the output, not the count.
            -h|--help) sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
            *) echo "setup-env.sh: unknown argument: $1" >&2; exit 2 ;;
        esac
    done

    if [ ! -f "$example_file" ]; then
        echo "setup-env.sh: no .env.example beside this script - run it from the repository" >&2
        exit 1
    fi

    # Now that every flag has been read. Through plan_set, so a --set naming the value the file
    # already holds is correctly nothing rather than a rewrite of the same line.
    if [ -n "$set_args" ]; then
        while IFS= read -r pair; do
            [ -n "$pair" ] || continue
            plan_set "${pair%%=*}" "${pair#*=}"
        done <<< "$set_args"
    fi

    if $no_prompt; then
        auto_answer
    elif ! $non_interactive; then
        if [ ! -t 0 ]; then
            echo "setup-env.sh: nothing to read answers from. Use --no-prompt to answer from" >&2
            echo "detection, or --set KEY=VALUE to configure explicitly." >&2
            exit 1
        fi

        say $"Homespool - .env setup"
        say
        if [ -f "$env_file" ]; then
            say $"Editing the existing file, and only the settings below are touched:"
        say_raw "    $env_file"
        else
            say $"No .env yet. One will be created from .env.example, with these settings filled in."
        fi

        ask_printer_host
        ask_user_host
        ask_timezone
        ask_ports
        ask_smtp
        ensure_go2rtc_credential
        check_subnet_collision
    fi

    if [ -z "$pending" ]; then
        say
        say $"Nothing to change."
        exit 0
    fi

    summarise
    warn_if_already_started

    if $dry_run; then
        say
        say $"--dry-run: nothing written."
        exit 0
    fi

    if ! $non_interactive && ! $no_prompt; then
        say
        ask_yes_no $"Write these" y || { say "Nothing written."; exit 0; }
    fi

    apply

    # A caller that did not ask for a walkthrough does not want to be told what to type next - the
    # unit that runs --no-prompt brings the stack up itself, on the line after this one.
    if ! $no_prompt; then
        say
        say $"Written. Bring the stack up with:"
        say
        say $"    docker compose up -d"
        say
    fi

}

# Sourced, this defines and does nothing; run, it is the script. The comparison is what tells the
# two apart - BASH_SOURCE[0] is this file either way, and $0 is the caller's name when sourced.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main "$@"
fi
