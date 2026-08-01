#!/bin/sh
#
# Installs the printer server block, but only when this deployment has a printer leaf to serve.
#
# The presence of the certificate is the whole decision, deliberately: PrusaConnect:PrinterTls is the
# application's setting, and copying it into the proxy's environment as well would be two settings
# for one fact - the failure this project keeps finding, where they disagree, every printer fails to
# connect, and neither value is wrong on its own so nothing can report it. The application issues a
# leaf when printer TLS is on and issues nothing when it is off, so the file answers the question
# without anyone having to keep two answers in step.
#
# 20-, so this runs before 25-self-signed-certificate.sh generates the *user* certificate. The two
# are unrelated - different certificate, different port, different authority - and the order only
# matters in that a failure here should be read before that script's output rather than after it.
set -eu

CERT_DIR=/etc/nginx/printer-certs
LEAF="$CERT_DIR/printer-leaf.pem"
KEY="$CERT_DIR/printer-leaf.key.pem"
SOURCE=/etc/nginx/homespool-printer.conf
TARGET=/etc/nginx/conf.d/homespool-printer.conf

# The application writes the leaf on its startup path, before it reports healthy, and compose.yaml
# holds this container back until it does - so by the time this runs the file is either there or was
# never going to be. The wait is for the case that ordering does not cover: `docker compose up proxy`
# on its own, or a stack where somebody has removed the depends_on. Short, because when printer TLS
# is off this is pure delay on a configuration that is a capture tool rather than a deployment.
WAITED=0
while [ ! -s "$LEAF" ] && [ "$WAITED" -lt 10 ]; do
    sleep 1
    WAITED=$((WAITED + 1))
done

if [ ! -s "$LEAF" ] || [ ! -s "$KEY" ]; then
    # Not an error. It is what PrusaConnect:PrinterTls=false looks like from in here, and in that
    # deployment printers dial the application's own port directly and this proxy is not in the path
    # at all. Said out loud anyway, because the other way to reach this line is an application that
    # failed to mint - and then printers would fail to connect with nothing pointing here.
    echo "$0: no printer leaf in $CERT_DIR, so the printer listener is NOT being served."
    echo "$0: that is expected when PrusaConnect:PrinterTls is false - printers then dial the"
    echo "$0: application's own published port and this proxy stays out of their path."
    echo "$0: if printer TLS IS meant to be on, the application did not write a leaf; check its log"
    echo "$0: for the certificate it issues at startup, then restart this container."
    exit 0
fi

cp "$SOURCE" "$TARGET"

echo "$0: serving the printer listener on 15443 with the leaf from $CERT_DIR."
