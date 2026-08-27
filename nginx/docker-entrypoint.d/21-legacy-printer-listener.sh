#!/bin/sh
#
# Installs the legacy plaintext printer server block, but only when this deployment has asked for one.
#
# LEGACY_PRINTER_PORT is the whole decision, and it is the same variable compose.yaml uses to open the
# application's own listener - so the listener and the block in front of it cannot disagree. That matters
# more here than anywhere else in this stack: a block without a listener behind it answers 502 on
# every printer message, and a listener without a block is a plaintext printer listener that the proxy
# never sees and nobody reading nginx's configuration would know exists.
#
# UNSET IS THE NORMAL CASE and is silent apart from one line. The published port then refuses
# connections, which is the correct answer for a deployment that owns no such printer.
#
# 21-, so it runs immediately after 20-printer-listener.sh: the two are read together when somebody is
# working out which printer listeners a deployment has open, and the numbering keeps them adjacent in the
# log. It needs no certificate and therefore none of that script's waiting - there is nothing to wait
# for, which is precisely what makes this listener available to a printer that cannot use the other one.
set -eu

SOURCE=/etc/nginx/homespool-legacy-printer.conf
TARGET=/etc/nginx/conf.d/homespool-legacy-printer.conf

if [ -z "${LEGACY_PRINTER_PORT:-}" ]; then
    echo "$0: LEGACY_PRINTER_PORT is not set, so no plaintext printer listener is served. This is the default."

    exit 0
fi

cp "$SOURCE" "$TARGET"

echo "$0: serving a PLAINTEXT printer listener on 15800, published as ${LEGACY_PRINTER_PORT}."
echo "$0: every printer provisioned onto it sends its token, its files and the PrusaLink password in"
echo "$0: its own INFO across the network in clear, and plain HTTP has no integrity, so gcode and"
echo "$0: commands can be altered in flight rather than only read. This is a LAN proposition: publish"
echo "$0: it to the internet knowingly or not at all. Printers whose firmware can load a custom"
echo "$0: certificate should be re-provisioned onto the TLS listener instead; unset LEGACY_PRINTER_PORT"
echo "$0: to close it."
