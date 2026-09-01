#!/bin/sh
#
# Writes one user-facing TLS server block per name this deployment answers to, into conf.d, at
# container start. The body of each is homespool-user-tls.conf, included rather than repeated.
#
# WHY GENERATED RATHER THAN WRITTEN OUT IN THE TEMPLATE: a server block carries exactly one
# certificate and USER_HOSTS is a list. One block naming every host worked while every host shared
# one self-signed certificate, and stopped working the moment one of them got a publicly-issued
# certificate - no authority will sign `homespool.lan`, so the public name and the LAN name need
# different certificates, and nginx chooses between them per server block, by SNI. Multiple
# ssl_certificate directives in one block are for offering the same names under different key
# algorithms, not for different names, so there is no way to express this in a fixed file.
#
# RUNS AFTER 25, and the number is what says so: 25 mints a certificate for any name that has none,
# so that by the time this script resolves a path there is always something to point at.
set -eu

CERT_DIR=/etc/nginx/certs
ACME_DIR="$CERT_DIR/certificates"
CONF_DIR=/etc/nginx/conf.d
BODY=/etc/nginx/homespool-user-tls.conf

# The same list 25-self-signed-certificate.sh minted certificates from, derived once by
# 16-user-server-names.envsh. Neither script splits USER_HOSTS itself, so the blocks written here
# and the certificates written there cannot disagree about what the names are.
NAMES="${USER_TLS_NAMES:-localhost}"

# A prefix of our own so the cleanup below cannot reach anything else in conf.d - the rendered
# template is default.conf, the transfer listener is bind-mounted in, and the printer listener is
# copied in by 20-printer-listener.sh.
PREFIX=homespool-user-

# Removed and rewritten every start rather than added to. conf.d is part of the image layer, not a
# volume, so it survives `docker compose restart` - and a name dropped from USER_HOSTS would
# otherwise keep its block, and keep being served, until the container was recreated. That is the
# failure this cleanup exists for: a name removed from the configuration that goes on answering.
rm -f "$CONF_DIR/$PREFIX"*.conf

if [ ! -f "$BODY" ]; then
    echo "$0: $BODY is missing - it is bind-mounted from ./nginx in compose.yaml." >&2
    echo "$0: no user-facing TLS listener will be configured." >&2
    exit 0
fi

written=0

OLD_IFS="$IFS"
IFS=';'
set -- $NAMES
IFS="$OLD_IFS"

for host in "$@"; do
    # The same check 25 applies, for the same reason: this name becomes a file path and a
    # server_name. A name that failed there has no certificate, so it could not be served anyway.
    case "$host" in
        *[!A-Za-z0-9.-]* | .* | '')
            echo "$0: refusing $host - not a usable hostname" >&2
            continue
            ;;
    esac

    # An issued certificate outranks the self-signed one beside it, and THIS IS THE WHOLE MECHANISM
    # by which a real certificate takes effect: an ACME client writes into certificates/ under the
    # name it was issued for, this script prefers that path on the next start, and nothing has to be
    # linked, copied or renamed into the place nginx reads. A renewal that rewrites the same file is
    # picked up by restarting the proxy.
    if [ -s "$ACME_DIR/$host.crt" ] && [ -s "$ACME_DIR/$host.key" ]; then
        crt="$ACME_DIR/$host.crt"
        key="$ACME_DIR/$host.key"
    elif [ -s "$CERT_DIR/$host.crt" ] && [ -s "$CERT_DIR/$host.key" ]; then
        crt="$CERT_DIR/$host.crt"
        key="$CERT_DIR/$host.key"
    else
        # Skipped rather than written with a path that does not exist. nginx refuses to start on a
        # missing ssl_certificate, which would take every other name down over one - the same trade
        # 20-printer-listener.sh makes for the printer block, and for the same reason.
        echo "$0: no certificate for $host - it will not be served over TLS." >&2
        continue
    fi

    cat > "$CONF_DIR/$PREFIX$host.conf" <<EOF
# Generated at container start by ${0##*/}. Edits here are lost on the next start - change
# homespool-user-tls.conf for the body, or USER_HOSTS in .env for which names exist.
server {
    listen 8443 ssl;
    http2 on;
    server_name $host;

    ssl_certificate     $crt;
    ssl_certificate_key $key;

    include $BODY;
}
EOF

    written=$((written + 1))
    echo "$0: $host served with ${crt#"$CERT_DIR/"}"
done

if [ "$written" -eq 0 ]; then
    echo "$0: no name could be served over TLS. Check USER_HOSTS in .env." >&2
fi
