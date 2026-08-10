#!/usr/bin/env bash
#
# Writes the handful of .env settings a deployment cannot guess, and leaves the rest alone.
#
#   ./setup-env.sh                          # ask, show what would change, then write
#   ./setup-env.sh --set PRINTER_HOST=...   # set keys directly, no questions
#   ./setup-env.sh --dry-run                # say what would change and write nothing
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
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$repo_root/.env"
example_file="$repo_root/.env.example"

dry_run=false
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

plan_set() {
    local key="$1" value="$2"
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

# Everything already spoken for on this machine: Docker's allocations and the host's own routes.
# One definition, because a range being free has to mean the same thing to the check that warns and
# to the search that proposes an alternative - and those were two concatenations that could drift.
allocated_ranges() {
    docker_subnets
    host_routes
}

# Every subnet Docker has allocated on this machine, one CIDR per line, EXCLUDING this stack's own
# network - which is matched by its compose label rather than by name, because the project name comes
# from the directory and a worktree or a -p flag changes it.
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

docker_subnets_uncached() {
    command -v docker >/dev/null 2>&1 || return 0

    local ours
    ours="$(docker network ls --filter label=com.docker.compose.network=homespool -q 2>/dev/null || true)"

    local id
    for id in $(docker network ls -q 2>/dev/null || true); do
        case " $ours " in
            *" $id "*) continue ;;
        esac
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
lan_addresses() {
    {
        if command -v ip >/dev/null 2>&1; then
            ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.*src \([0-9.]*\).*/\1/p'
            hostname -I 2>/dev/null | tr ' ' '\n'
        else
            local iface
            iface="$(route -n get default 2>/dev/null | awk '/interface:/{print $2}')"
            [ -n "$iface" ] && ipconfig getifaddr "$iface" 2>/dev/null
            ifconfig 2>/dev/null | awk '/inet /{print $2}'
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
# and loud, on the same reasoning homespool-firstboot.sh gives for excluding the block outright -
# and here the operator can still type an address by hand, so nothing is actually blocked.
unreachable_ranges() {
    local subnets
    subnets="$(docker_subnets)"
    if [ -n "$subnets" ]; then
        echo "$subnets"
        return 0
    fi
    if ! $docker_unavailable_warned; then
        warn "Could not ask Docker which ranges it has allocated - is it installed and running?"
        warn "Falling back to excluding 172.16.0.0/12 entirely. If your LAN genuinely lives there,"
        warn "type the address rather than picking from the list."
        docker_unavailable_warned=true
    fi
    echo "172.16.0.0/12"
}

docker_unavailable_warned=false

# Loopback and link-local are never reachable by a printer either, and need no daemon to recognise.
filter_unusable() {
    local subnets addr
    subnets="$(unreachable_ranges)"
    while read -r addr; do
        [ -n "$addr" ] || continue
        case "$addr" in
            127.*|169.254.*|0.0.0.0) continue ;;
        esac
        overlaps_any "$addr/32" <<< "$subnets" >/dev/null || echo "$addr"
    done
}

dedupe() {
    awk '!seen[$0]++'
}

detect_timezone() {
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
    read -r answer || true
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
        answer="$(ask "$prompt (y/n)" "$default")"
        case "$answer" in
            [Yy]|[Yy][Ee][Ss]) return 0 ;;
            [Nn]|[Nn][Oo]) return 1 ;;
            *) echo "  Please answer y or n." >&2 ;;
        esac
    done
}

say() { echo "$@" >&2; }
warn() { echo "  ! $*" >&2; }

# ------------------------------------------------------------------------------------------------
# The questions
# ------------------------------------------------------------------------------------------------

ask_printer_host() {
    say
    say "The address printers use to reach this server."
    say
    say "  This is the one setting with no sensible default, and the one that is expensive to get"
    say "  wrong: it is written into every USB provisioning bundle, and the printer certificate is"
    say "  issued once - on the first start - covering this address and every address the machine"
    say "  can see. Set it now and it is covered by construction."
    say

    local current candidates choice n i addr
    current="$(env_get PRINTER_HOST)"
    candidates="$(lan_addresses)"

    if [ -n "$candidates" ]; then
        say "  Addresses on this machine that a printer could reach:"
        n=0
        while read -r addr; do
            [ -n "$addr" ] || continue
            n=$((n + 1))
            say "    $n) $addr"
        done <<< "$candidates"
        say "    $((n + 1))) something else - a name, or an address not listed"
        say

        choice="$(ask "  Which" "${current:-1}")"
        # A bare number picks from the list; anything else is taken literally, so an operator who
        # already knows their hostname can simply type it rather than working out which option means
        # "let me type it".
        case "$choice" in
            ''|*[!0-9]*) : ;;
            *)
                if [ "$choice" -ge 1 ] && [ "$choice" -le "$n" ]; then
                    choice="$(echo "$candidates" | sed -n "${choice}p")"
                elif [ "$choice" -eq $((n + 1)) ]; then
                    choice="$(ask "  Address or name")"
                fi
                ;;
        esac
    else
        warn "No usable address detected on this machine."
        warn "Loopback, link-local and Docker's own ranges are excluded - a printer cannot reach any"
        warn "of them, and one baked into the certificate cannot be corrected without a reissue."
        choice="$(ask "  Address or name" "$current")"
    fi

    validate_printer_host "$choice" || return 0
    plan_set PRINTER_HOST "$choice"
}

validate_printer_host() {
    local host="$1" resolved hit

    if [ -z "$host" ]; then
        warn "Left unset. USB-key provisioning will refuse to produce a snippet until it is."
        return 1
    fi

    case "$host" in
        localhost|127.*|0.0.0.0)
            warn "$host is this machine talking to itself - no printer can reach it."
            ask_yes_no "  Use it anyway" n || return 1
            ;;
    esac

    # A name rather than an address: resolve it, and say what the certificate will end up covering.
    # A stale DNS entry is invisible until a printer fails to verify, and by then the certificate is
    # already issued.
    case "$host" in
        *[!0-9.]*)
            resolved="$(resolve_host "$host")"
            if [ -z "$resolved" ]; then
                warn "$host does not resolve from here. The certificate will cover the name, but a"
                warn "printer that cannot resolve it either will have nothing to connect to."
                ask_yes_no "  Use it anyway" y || return 1
            else
                say "  $host resolves to $resolved - the certificate will cover both."
            fi
            ;;
    esac

    # The same ranges the offered list was filtered against, so a typed address is judged by the
    # rule a picked one already passed.
    hit="$(overlaps_any "${resolved:-$host}/32" <<< "$(unreachable_ranges)" | head -1)"
    if [ -n "$hit" ]; then
        warn "$host is inside $hit, which printers on your network cannot route to. It would be"
        warn "minted into the certificate and frozen there."
        ask_yes_no "  Use it anyway" n || return 1
    fi

    return 0
}

resolve_host() {
    if command -v getent >/dev/null 2>&1; then
        getent hosts "$1" 2>/dev/null | awk 'NR == 1 { print $1 }'
    else
        # macOS has no getent. dscacheutil goes through the same resolver the OS uses, so it agrees
        # with /etc/hosts and mDNS where a bare DNS query would not.
        dscacheutil -q host -a name "$1" 2>/dev/null | awk '/^ip_address:/ { print $2; exit }'
    fi
}

ask_user_host() {
    say
    local current suggestion
    current="$(env_get USER_HOST)"
    suggestion="$current"
    if [ "$current" = localhost ]; then
        suggestion="$(hostname 2>/dev/null || echo localhost)"
        case "$suggestion" in
            *.*) : ;;
            *) suggestion="$suggestion.local" ;;
        esac
    fi
    say "The name people type in a browser. Cosmetic - it names the self-signed certificate, and a"
    say "browser warns about that certificate whatever name it carries."
    plan_set USER_HOST "$(ask "  Name" "$suggestion")"
}

ask_timezone() {
    say
    say "The zone timestamps are shown in. The conversion happens on the server, so this decides what"
    say "print history reads and what an invitation email says its expiry is."
    plan_set TZ "$(ask "  Timezone" "$(prefer_current TZ "$(detect_timezone)")")"
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
    say "  Plain-HTTP visitors are redirected to HTTPS, and the redirect has to name the port a"
    say "  BROWSER should ask for - which is not necessarily the one published here, if anything"
    say "  forwards to this machine."
    plan_set HTTPS_PORT_SUFFIX "$(ask "  Port suffix in the redirect" "$suffix")"
}

ask_smtp() {
    say
    say "Outgoing mail is optional. Without it, new accounts are created already confirmed and"
    say "password reset is unavailable - invitations still work, you just pass the link on yourself."
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

    plan_set SMTP_HOST "$(ask "  Mail server" "$current_host")"

    # One question for two settings, because they are one decision. Asked separately, the pair can
    # be made to disagree - implicit TLS on 587, STARTTLS on 465 - and both halves of that fail in
    # ways that read as a broken server rather than a wrong setting.
    local mode port implicit
    mode="$(ask "  465 for implicit TLS, 587 for STARTTLS, 25 for unencrypted" "$(env_get SMTP_PORT)")"
    port="$mode"
    case "$mode" in
        465) implicit=true ;;
        *)   implicit=false ;;
    esac
    plan_set SMTP_PORT "$port"
    plan_set SMTP_USE_IMPLICIT_TLS "$implicit"

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
        warn "No source of randomness found - leaving the camera sidecar unauthenticated, which is"
        warn "what this deployment already had. Its port is not published."
        return 0
    fi

    # Both or neither, always. A username with an empty password switches the sidecar's
    # authentication on with an empty key and locks Homespool out along with everyone else.
    plan_set GO2RTC_USERNAME homespool
    plan_set GO2RTC_PASSWORD "$password"
    say "Generated a credential for the camera sidecar."
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
    colliding="$(echo "$colliding" | tr '\n' ' ')"

    say
    warn "The compose network $subnet collides with: $colliding"
    warn "A collision with another Docker network fails loudly at startup. A collision with a route"
    warn "this machine already has does not: the stack comes up, and that network stops being"
    warn "reachable from here."

    candidate="$(free_subnet)"
    if [ -z "$candidate" ]; then
        warn "Every /16 from 172.16 to 172.31 is in use here - pick a range by hand."
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
                --filter label=com.docker.compose.volume=printerservice-data 2>/dev/null)" ] || return 0

    say
    warn "This stack has run before, so the printer certificate has already been issued - and it is"
    warn "issued once. Changing PRINTER_HOST in .env does not change what that certificate covers."
    warn "Reissue it afterwards from Admin -> Printer certificate, or printers will fail to verify."
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
    local key value before
    say
    say "This will change $env_file:"
    say
    while IFS= read -r line; do
        [ -n "$line" ] || continue
        key="${line%%=*}"
        value="${line#*=}"
        before="$(env_get "$key")"
        if ! key_present "$env_file" "$key"; then
            before="(unset, default $(display_value "$key" "$before"))"
        else
            before="$(display_value "$key" "$before")"
        fi
        printf '    %-24s %s  ->  %s\n' "$key" "$before" "$(display_value "$key" "$value")" >&2
    done <<< "$pending"
    say
    say "Every other line - comments, blank lines, and any key not listed - is left as it is."
}

apply() {
    # Seeded from the example rather than written from nothing, so that somebody who opens this file
    # later still finds the documentation for the twenty-odd settings the wizard never asked about.
    if [ ! -f "$env_file" ]; then
        cp "$example_file" "$env_file"
        say "Created $env_file from .env.example."
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
                    # Through plan_set, so that --set with the value the file already holds is correctly
                    # nothing rather than a rewrite of the same line - which is what makes this safe to
                    # run unconditionally from another script on every boot.
                    *=*) plan_set "${2%%=*}" "${2#*=}"; non_interactive=true; shift 2 ;;
                    *) echo "setup-env.sh: --set wants KEY=VALUE" >&2; exit 2 ;;
                esac
                ;;
            --dry-run) dry_run=true; shift ;;
            # 2,8 rather than a fixed larger range: the usage block ends at the --dry-run line, and a
            # range that runs past it prints half a paragraph about compose defaults. Extend when the
            # header does.
            -h|--help) sed -n '2,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
            *) echo "setup-env.sh: unknown argument: $1" >&2; exit 2 ;;
        esac
    done

    if [ ! -f "$example_file" ]; then
        echo "setup-env.sh: no .env.example beside this script - run it from the repository" >&2
        exit 1
    fi

    if ! $non_interactive; then
        if [ ! -t 0 ]; then
            echo "setup-env.sh: nothing to read answers from. Use --set KEY=VALUE to configure" >&2
            echo "non-interactively." >&2
            exit 1
        fi

        say "Homespool - .env setup"
        say
        if [ -f "$env_file" ]; then
            say "Editing the existing $env_file. Only the settings below are touched."
        else
            say "No .env yet. One will be created from .env.example, with these settings filled in."
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
        say "Nothing to change."
        exit 0
    fi

    summarise
    warn_if_already_started

    if $dry_run; then
        say
        say "--dry-run: nothing written."
        exit 0
    fi

    if ! $non_interactive; then
        say
        ask_yes_no "Write these" y || { say "Nothing written."; exit 0; }
    fi

    apply

    say
    say "Written. Bring the stack up with:"
    say
    say "    docker compose up -d"
    say

}

# Sourced, this defines and does nothing; run, it is the script. The comparison is what tells the
# two apart - BASH_SOURCE[0] is this file either way, and $0 is the caller's name when sourced.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main "$@"
fi
