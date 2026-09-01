#!/bin/sh
# Warn before a publicly-trusted certificate expires.
#
# Run daily from homespool-check-cert.timer. The renewal runs unattended and every way it can fail
# is quiet: a revoked provider credential, a certificate written where the proxy does not look, a
# provider that renamed an environment variable, a timer that was never enabled. None of those
# produce a symptom until the certificate expires and every browser refuses the site at once. This
# is the thing that notices.
#
# Exit 0 = fine, 1 = inside the warning window, 2 = expired, missing or unreadable. Quiet on
# success, so the timer only speaks when something is wrong.
#
# READ THROUGH THE PROXY'S OWN IMAGE, as the proxy's own uid. Two reasons, and neither is
# incidental: the certs image is Alpine and has no openssl at all, and "root on the host can read
# it" is not the question - "can the process that serves it read it" is. Running this in the image
# that serves the certificate, as the user that serves it, is the only version of this check that
# answers the question actually being asked.
#
# ONE CONTAINER FOR ALL NAMES. A container start is seconds on a small board, and a check that
# costs a start per name is a check somebody turns off.
set -eu

COMPOSE_DIR="${COMPOSE_DIR:-/opt/homespool}"

# How long before expiry to start complaining. Let's Encrypt issues for 90 days and lego renews at
# 30 remaining, so 21 leaves a week of failed renewals before anybody is told - long enough that a
# single bad night is not an alert, short enough to fix by hand if it is real.
WARN_DAYS="${WARN_DAYS:-21}"

cd "$COMPOSE_DIR"

acme_hosts="$(docker compose --profile certs run --rm --entrypoint sh certs \
    -c 'printf %s "${ACME_HOSTS:-}"' 2>/dev/null | tr -d '\r' || true)"

if [ -z "$acme_hosts" ]; then
    # Not a fault. A deployment with no ACME_HOSTS serves self-signed certificates valid for ten
    # years, and there is nothing here to expire.
    exit 0
fi

# Names are interpolated into a shell command below, so they are checked first. A name outside the
# hostname character set cannot have been issued a certificate anyway - no authority would sign it.
checked=''
OLD_IFS="$IFS"
IFS=';'
set -- $acme_hosts
IFS="$OLD_IFS"

for host in "$@"; do
    host="$(echo "$host" | tr -d '[:space:]')"
    [ -n "$host" ] || continue
    case "$host" in
        *[!A-Za-z0-9.-]* | .*)
            echo "CRITICAL: $host is not a usable hostname; check ACME_HOSTS in .env"
            exit 2
            ;;
    esac
    checked="$checked$host "
done

[ -n "$checked" ] || exit 0

# One pass inside the proxy image: for each name, print `host<space>notAfter` or `host MISSING`.
# --no-deps matters - the proxy declares depends_on, and without it this would start the whole
# application stack to read a file.
report="$(docker compose run --rm --no-deps --entrypoint sh proxy -c '
    for h in '"$checked"'; do
        c="/etc/nginx/certs/certificates/$h.crt"
        if [ -s "$c" ] && d=$(openssl x509 -in "$c" -noout -enddate 2>/dev/null); then
            echo "$h ${d#notAfter=}"
        else
            echo "$h MISSING"
        fi
    done' 2>/dev/null | tr -d '\r' || true)"

if [ -z "$report" ]; then
    echo "CRITICAL: could not read any certificate through the proxy image."
    echo "          Is the stack built? Check: docker compose ps"
    exit 2
fi

status=0

# Parsed in this shell rather than in a pipeline: a `while read` on the right of a pipe runs in a
# subshell, where $status would be set and then discarded, and this would report success however
# many certificates had expired.
saved_ifs="$IFS"
IFS='
'
for line in $report; do
    IFS="$saved_ifs"

    host="${line%% *}"
    end="${line#* }"

    if [ "$end" = MISSING ]; then
        echo "CRITICAL: $host has no readable certificate under certificates/"
        echo "          The proxy is serving the self-signed certificate for this name."
        echo "          Check: journalctl -u homespool-renew-cert.service -n 50"
        status=2
        IFS='
'
        continue
    fi

    if ! end_epoch="$(date -d "$end" +%s 2>/dev/null)"; then
        echo "CRITICAL: unparseable expiry on $host's certificate: $end"
        status=2
        IFS='
'
        continue
    fi

    days=$(( (end_epoch - $(date +%s)) / 86400 ))

    if [ "$days" -lt 0 ]; then
        echo "CRITICAL: $host's certificate EXPIRED $(( -days )) days ago ($end)"
        status=2
    elif [ "$days" -le "$WARN_DAYS" ]; then
        echo "WARNING: $host's certificate expires in $days days ($end)"
        echo "         Check: systemctl status homespool-renew-cert.timer"
        echo "                journalctl -u homespool-renew-cert.service -n 50"
        [ "$status" -eq 2 ] || status=1
    fi

    IFS='
'
done
IFS="$saved_ifs"

exit "$status"
