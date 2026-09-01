#!/bin/sh
# Obtain or renew a publicly-trusted certificate for every name in ACME_HOSTS, and restart the proxy
# only if one actually changed.
#
# Run daily from homespool-renew-cert.timer. `lego run` is "get or renew": it exits without
# contacting the certificate authority at all until a certificate is inside its renewal window, so
# running this every day costs one container start per name and nothing else. Comparing the
# certificates either side is what keeps the proxy from being restarted on the ~89 of 90 runs that
# do nothing.
#
# ONE RUN PER NAME, and this is the part that is easy to get wrong: lego names the files it writes
# after the FIRST domain it was given, so a single run naming three domains produces one certificate
# in one file. The other two names would find nothing under their own name and be served the
# self-signed certificate instead - working, warning in every browser, and with nothing anywhere
# saying why. A name at a time means each gets its own file, which is what the proxy looks for.
#
# NOTHING IS LINKED OR COPIED. lego writes into certificates/ inside the certificate volume, and
# that is where the proxy's 26-user-tls-servers.sh looks before it looks at the self-signed
# certificate beside it. Obtaining a certificate for a name is the whole of the work; making it the
# one that gets served happens by itself at the next proxy start.
set -eu

# Where the compose file lives. Set by the systemd unit; the default is where install.sh puts a
# deployment that did not say otherwise.
COMPOSE_DIR="${COMPOSE_DIR:-/opt/homespool}"

# The uid the proxy runs as, and therefore the identity that has to be able to read a certificate
# for it to be worth anything. The certs service runs as this too, so everything below reads exactly
# as the proxy will.
NGINX_UID=101

cd "$COMPOSE_DIR"

compose() {
    docker compose --profile certs "$@"
}

# A shell in the certs service, which mounts the certificate volume at /certs and runs as the proxy's
# uid. Used rather than `docker run -v <volume>` so the volume's name is never written down: it is
# derived from the compose project, which is derived from the directory name, and a deployment in a
# differently-named directory would otherwise have this script quietly create and inspect an empty
# volume of its own.
in_certs() {
    compose run --rm --entrypoint sh certs -c "$1" 2>/dev/null
}

# Every issued certificate, hashed. Read as the proxy's uid deliberately: "root can read it" is not
# the question, "can nginx read it" is. A certificate this cannot read hashes to nothing, which shows
# up as a change below and, more importantly, as an empty verification afterwards.
fingerprint() {
    in_certs 'cd /certs/certificates 2>/dev/null && sha256sum ./*.crt 2>/dev/null | sort || true'
}

# What compose itself resolved, rather than this script parsing .env. The two are not reliably the
# same thing - compose applies defaults, and a value exported in the environment beats the file - and
# a list read one way and used another is the kind of disagreement that produces a certificate for a
# name nobody asked for.
acme_hosts="$(in_certs 'printf %s "${ACME_HOSTS:-}"' | tr -d '\r' || true)"

if [ -z "$acme_hosts" ]; then
    echo "ACME_HOSTS is empty - nothing to renew."
    echo "This deployment serves the self-signed certificates the proxy mints, which is a complete"
    echo "configuration. Set ACME_HOSTS in .env only if you have a name a public authority can verify."
    exit 0
fi

before="$(fingerprint)"

failed=0

OLD_IFS="$IFS"
IFS=';'
set -- $acme_hosts
IFS="$OLD_IFS"

for host in "$@"; do
    host="$(echo "$host" | tr -d '[:space:]')"
    [ -n "$host" ] || continue

    echo "--- $host ---"

    # LEGO_DOMAINS overridden per name; everything else - the provider, the credentials, the
    # account - comes from the service definition and /etc/lego/dns.env.
    #
    # A failure here is recorded and the next name is still attempted: one name whose DNS provider
    # is misconfigured should not stop a second name from renewing, and the unit still fails at the
    # end so the timer reports it.
    if ! compose run --rm -e "LEGO_DOMAINS=$host" certs; then
        echo "FAILED to obtain or renew a certificate for $host" >&2
        failed=1
    fi
done

after="$(fingerprint)"

# Verify what the proxy will actually find, for every name that was asked for. This is the check
# that catches the quiet failures: a certificate written somewhere the proxy does not look, or
# written with ownership it cannot read. Both leave a deployment serving a self-signed certificate
# and reporting perfect health.
missing=''
for host in "$@"; do
    host="$(echo "$host" | tr -d '[:space:]')"
    [ -n "$host" ] || continue

    if ! in_certs "[ -s /certs/certificates/$host.crt ] && [ -s /certs/certificates/$host.key ]"; then
        missing="$missing$host "
    fi
done

if [ -n "$missing" ]; then
    echo "ERROR: uid $NGINX_UID cannot read a certificate for: $missing" >&2
    echo "       The proxy will serve the self-signed certificate for those names." >&2
    in_certs 'ls -l /certs /certs/certificates 2>&1' >&2 || true
    failed=1
fi

if [ "$before" != "$after" ]; then
    echo "certificates changed - restarting proxy"

    # A restart rather than a reload, because the server blocks themselves are rewritten at start:
    # 26-user-tls-servers.sh is what decides that a name is now served its issued certificate
    # instead of its self-signed one, and it runs from the entrypoint.
    docker compose restart proxy
else
    echo "no change"
fi

exit "$failed"
