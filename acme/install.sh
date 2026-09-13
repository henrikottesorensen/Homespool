#!/bin/sh
# Install the automatic certificate renewal for a Homespool deployment.
#
#   sudo ./acme/install.sh [/path/to/deployment]
#
# The path is the directory holding compose.yaml and .env; it defaults to /opt/homespool. Nothing
# here is required to run Homespool - a deployment without it serves the self-signed certificates
# the proxy mints for itself, which works and warns in the browser. This removes the warning for a
# name that a public certificate authority can verify.
#
# Idempotent: run it again after changing the deployment path, or to repair an installation.
set -eu

COMPOSE_DIR="${1:-/opt/homespool}"

SBIN=/usr/local/sbin
UNITS=/etc/systemd/system
CREDS=/etc/lego/dns.env

here="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
    echo "This installs systemd units and a credentials file; run it with sudo." >&2
    exit 1
fi

if [ ! -f "$COMPOSE_DIR/compose.yaml" ]; then
    echo "No compose.yaml in $COMPOSE_DIR." >&2
    echo "Pass the deployment directory: sudo $0 /path/to/deployment" >&2
    exit 1
fi

# WHO CAN CHANGE WHAT ROOT WILL RUN. The timers run `docker compose` as root from this directory, and
# compose will run whatever the files there describe - a privileged container with / mounted is one
# edit away. So everything compose reads must be writable only by accounts that can become root
# already: root, and members of the docker group, which is root-equivalent. An owner in that group
# gains nothing by this, which is what lets a deployment stay editable by its operator - the Pi's
# device user is one.
#
# What compose reads: the directory (a writer can add an override file), every directory above it (a
# writer can rename it away and put another in its place), compose.yaml, .env, and any override file
# compose merges without being asked. nginx/ and the build contexts are not in the set: a hostile
# image is root in a container, not on this machine.
#
# Group write is not refused on its own. Debian and Raspberry Pi OS log in with umask 0002, so an
# ordinary checkout is group-writable under its owner's private group; the question is whether every
# member of that group is trusted, including the accounts whose primary group it is.
#
# Checked here, once, and not by the timers. Only root can hand a file to another account after
# this, and a timer refusing would stop the renewal and the expiry check that warns about it at the
# same time, leaving a certificate to run out with nothing said.
#
# Not covered: an ACL granting write beyond the mode bits, primary-group members a directory service
# declines to enumerate, and a symlink reached through a directory that is itself a link to a link.

# 0 if the account on this passwd line can already become root.
trusted_account() {
    [ "$(printf '%s' "$1" | cut -d: -f3)" = 0 ] && return 0
    case " $(id -Gn -- "$(printf '%s' "$1" | cut -d: -f1)" 2>/dev/null) " in
        *" docker "*) return 0 ;;
    esac
    return 1
}

trusted_uid() {
    [ "$1" = 0 ] && return 0
    tu_line="$(getent passwd "$1")" || return 1
    trusted_account "$tu_line"
}

# A group nobody names is refused rather than assumed empty.
trusted_gid() {
    tg_group="$(getent group "$1")" || return 1
    for tg_name in $(printf '%s' "$tg_group" | cut -d: -f4 | tr ',' ' '); do
        tg_line="$(getent passwd "$tg_name")" || return 1
        trusted_account "$tg_line" || return 1
    done
    getent passwd | while IFS= read -r tg_line; do
        [ "$(printf '%s' "$tg_line" | cut -d: -f4)" = "$1" ] || continue
        trusted_account "$tg_line" || exit 1
    done
}

refusals=''

# Once per path and reason: the literal and resolved paths share most of their ancestors.
refuse() {
    case "$refusals" in
        *"  $1
"*) return ;;
    esac
    refusals="$refusals  $1
"
}

# $2 is "ancestor" for a directory above the deployment, or above a file compose reads through a
# symlink. A sticky ancestor - /tmp - may be writable by others, because nobody but its owner can
# rename an entry out of it, and the entry's own owner is checked at the next level down. The
# deployment directory gets no such allowance: creating a file is exactly what the sticky bit
# permits.
#
# -L, because a path walked as written can pass through a symlink, and a link's own mode says nothing
# about who can write the directory it names.
check_entry() {
    ce_stat="$(stat -L -c '%u %g %a' -- "$1")"
    ce_owner="${ce_stat%% *}"
    ce_rest="${ce_stat#* }"
    ce_group="${ce_rest%% *}"
    ce_mode="0${ce_rest#* }"

    ce_sticky_ancestor=''
    if [ "$2" = ancestor ] && [ $((ce_mode & 01000)) -ne 0 ]; then
        ce_sticky_ancestor=y
    fi

    if ! trusted_uid "$ce_owner"; then
        refuse "$1 is owned by uid $ce_owner, which is neither root nor in the docker group"
    fi
    if [ $((ce_mode & 0002)) -ne 0 ] && [ -z "$ce_sticky_ancestor" ]; then
        refuse "$1 is writable by every account"
    fi
    if [ $((ce_mode & 0020)) -ne 0 ] && [ -z "$ce_sticky_ancestor" ] && ! trusted_gid "$ce_group"; then
        refuse "$1 is writable by group $ce_group, which has a member outside the docker group"
    fi
}

check_ancestors() {
    ca_path="$1"
    while [ "$ca_path" != / ]; do
        ca_path="$(dirname -- "$ca_path")"
        check_entry "$ca_path" ancestor
    done
}

# The path as written and the path it resolves to. The units cd into the first, so a directory
# holding a symlink along it decides where compose runs as surely as the directory it points at.
deployment="$(cd -P -- "$COMPOSE_DIR" && pwd -P)"
check_entry "$deployment" deployment
check_ancestors "$deployment"
check_ancestors "$(cd -L -- "$COMPOSE_DIR" && pwd -L)"

for f in compose.yaml .env \
         compose.override.yaml compose.override.yml \
         docker-compose.override.yaml docker-compose.override.yml; do
    candidate="$deployment/$f"
    [ -e "$candidate" ] || [ -L "$candidate" ] || continue

    # A symlink is followed to what compose will actually read. One pointing at nothing is refused:
    # whoever can create its target decides what compose reads.
    if ! target="$(realpath -e -- "$candidate" 2>/dev/null)"; then
        refuse "$candidate is a symlink to a file that does not exist"
        continue
    fi

    check_entry "$target" file
    check_ancestors "$target"
    if [ -L "$candidate" ]; then
        link="$(readlink -- "$candidate")"
        case "$link" in
            /*) ;;
            *) link="$deployment/$link" ;;
        esac
        # -s: .. tidied away without following a link, so the path stays the one as written.
        check_ancestors "$(realpath -s -- "$link")"
    fi
done

if [ -n "$refusals" ]; then
    echo "Refusing to install. The timers run docker compose as root from $COMPOSE_DIR, so anyone" >&2
    echo "who can change what compose reads there can become root. Only root and members of the" >&2
    echo "docker group, who can become root already, may be able to write:" >&2
    printf '%s\n' "$refusals" >&2
    echo "Give each path to root or to an account in the docker group, or take the write permission" >&2
    echo "away - for example: sudo chown root: <path> && sudo chmod go-w <path>" >&2
    echo "Then run this again. This run has installed and changed nothing." >&2
    exit 1
fi

command -v systemctl >/dev/null || {
    echo "No systemctl here. The two scripts in $here work standalone - run them from cron or a" >&2
    echo "timer of your own, with COMPOSE_DIR=$COMPOSE_DIR in the environment." >&2
    exit 1
}

echo "Installing for the deployment in $COMPOSE_DIR"

# Without the .sh, because these become commands rather than files somebody edits. 0755: they read
# no secrets themselves - the credentials are read by the container, from the file below.
for s in homespool-renew-cert homespool-check-cert; do
    install -m 0755 "$here/$s.sh" "$SBIN/$s"
    echo "  $SBIN/$s"
done

# The deployment path is baked in here rather than read from a file at run time, so that a machine
# running two deployments can carry two differently-named copies of these units without either
# knowing about the other's configuration.
for u in homespool-renew-cert.service homespool-renew-cert.timer \
         homespool-check-cert.service homespool-check-cert.timer; do
    sed "s#@COMPOSE_DIR@#$COMPOSE_DIR#g" "$here/$u" > "$UNITS/$u"
    chmod 0644 "$UNITS/$u"
    echo "  $UNITS/$u"
done

# The credentials the DNS provider wants, which is the one thing this script cannot fill in. Created
# empty rather than left absent: the file being there, with the right mode, is what makes "put your
# token in it" a complete instruction.
#
# OUTSIDE THE CHECKOUT, and that is the point of the path. A credential inside the repository is one
# `git add -A` away from being published, and this repository is public.
if [ ! -f "$CREDS" ]; then
    mkdir -p "$(dirname "$CREDS")"
    cat > "$CREDS" <<'EOF'
# Credentials for the DNS provider that answers the ACME challenge, as environment variables.
#
# Which names go here depends entirely on the provider named by ACME_DNS_PROVIDER in .env. Ask lego
# what it wants, from the deployment directory:
#
#   sudo docker compose --profile certs run --rm certs dnshelp -c cloudflare
#
# Cloudflare, with a token scoped to Zone:DNS:Edit on the one zone:
#
#   CLOUDFLARE_DNS_API_TOKEN=...
#
# Any other lego setting can go here too - every flag has an environment variable. The one worth
# knowing about is LEGO_DNS_RESOLVERS, for a network whose resolver answers differently from the
# public internet:
#
#   LEGO_DNS_RESOLVERS=1.1.1.1:53
EOF
    chmod 0600 "$CREDS"
    echo "  $CREDS (created, empty - put your provider's credentials in it)"
else
    chmod 0600 "$CREDS"
    echo "  $CREDS (already present, left alone)"
fi

systemctl daemon-reload
systemctl enable --now homespool-renew-cert.timer homespool-check-cert.timer >/dev/null
echo "  timers enabled"

echo
echo "Installed. Before the first renewal can work:"
echo
echo "  1. Put your DNS provider's credentials in $CREDS"
echo "  2. Set ACME_HOSTS, ACME_EMAIL and ACME_DNS_PROVIDER in $COMPOSE_DIR/.env"
echo "     (setup-env.sh will ask, or edit the file directly)"
echo "  3. Run it once by hand rather than waiting for the timer:"
echo
echo "       sudo systemctl start homespool-renew-cert.service"
echo "       journalctl -u homespool-renew-cert.service -n 50"
echo
echo "A name only gets a publicly-trusted certificate if it is in BOTH ACME_HOSTS and USER_HOSTS."
echo "Names that are not in ACME_HOSTS keep their self-signed certificate, which is correct for"
echo "anything a public authority cannot verify - a .lan name or a bare address."
