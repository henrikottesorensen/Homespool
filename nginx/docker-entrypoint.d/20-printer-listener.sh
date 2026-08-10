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

# How long the background watcher below keeps looking. Generous: it is covering a first boot on a
# Raspberry Pi, where the application migrates its database before it mints anything, and the cost of
# waiting too long is a log line nobody reads.
WATCH_SECONDS=600

# ---------------------------------------------------------------------------------------------
# The fast path, and the only path this script had until the proxy stopped waiting for health.
#
# This container no longer has `condition: service_healthy` on the application (compose.yaml), so
# that it can serve a holding page during startup instead of refusing connections. The consequence
# lands here: "no leaf" used to mean "printer TLS is off", because the application had already
# reported healthy and would therefore have written one. It no longer means that - it may equally
# mean the application is three minutes into its first migration.
#
# So the short wait stays for the common case of a restart, where the leaf is already in the volume
# and this returns immediately; and everything else moves to a background watcher.
# ---------------------------------------------------------------------------------------------
WAITED=0
while [ ! -s "$LEAF" ] && [ "$WAITED" -lt 10 ]; do
    sleep 1
    WAITED=$((WAITED + 1))
done

if [ -s "$LEAF" ] && [ -s "$KEY" ]; then
    cp "$SOURCE" "$TARGET"
    echo "$0: serving the printer listener on 15443 with the leaf from $CERT_DIR."
    exit 0
fi

# ---------------------------------------------------------------------------------------------
# No leaf yet. Do not block the entrypoint - nginx has to start now, or there is no holding page and
# the whole point of ungating is lost. Watch in the background and reload when it appears.
#
# Backgrounded from a hook rather than run as a sidecar because it needs to be the *same* nginx it
# reloads. The entrypoint execs nginx after the hooks run, so this subshell outlives this script and
# keeps running alongside the server.
# ---------------------------------------------------------------------------------------------
echo "$0: no printer leaf yet; nginx starts now and will pick one up if it appears."

(
    ELAPSED=0
    while [ ! -s "$LEAF" ] || [ ! -s "$KEY" ]; do
        if [ "$ELAPSED" -ge "$WATCH_SECONDS" ]; then
            # Reached only after ten minutes of no certificate, which is the honest moment to say
            # the things this script used to say immediately.
            echo "$0: still no printer leaf in $CERT_DIR after ${WATCH_SECONDS}s."
            echo "$0: the printer listener is NOT being served."
            echo "$0: that is expected when PrusaConnect:PrinterTls is false - printers then use"
            echo "$0: the application's own published port and this proxy stays out of their path."
            echo "$0: if printer TLS IS meant to be on, the application did not write a leaf; check"
            echo "$0: its log for the certificate it issues at startup, then restart this container."
            exit 0
        fi
        sleep 5
        ELAPSED=$((ELAPSED + 5))
    done

    cp "$SOURCE" "$TARGET"

    # -t first. A reload with a broken configuration leaves the running server on its old one and
    # logs a failure, so the site would keep working while printers silently never got a listener -
    # the exact class of quiet failure this file's header is about.
    if nginx -t 2>/dev/null; then
        nginx -s reload
        echo "$0: printer leaf appeared after ${ELAPSED}s; printer listener now served on 15443."
    else
        echo "$0: printer leaf appeared but the configuration does not validate; NOT reloading."
        nginx -t || true
    fi
) &

exit 0
