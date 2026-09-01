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
