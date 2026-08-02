#!/bin/sh
# Brings the Homespool stack up on a freshly-flashed card, and keeps it up on every boot after.
#
# Everything here is idempotent and re-run each boot, which is why there is no "have I run before"
# stamp file. The two things that must happen exactly once guard themselves on their own evidence:
# the images are loaded while the tarball is still there, and .env is written only when absent.
set -eu

cd /opt/homespool

# ---------------------------------------------------------------------------------------------
# The container images are ALREADY in /var/lib/docker - pi/build.sh loaded them into this card's
# Docker store while building it, so there is normally nothing to do here.
#
# That is deliberate: unpacking ~550 MB of layers is identical work producing an identical result on
# every card, so paying for it once at build time beats paying for it on every install, in Pi 3 CPU
# and SD-card writes.
#
# This block therefore only fires if somebody deliberately stages a tarball - a hand-built card, or
# a rescue after a bad bake. It is not the normal path, and no tarball ships.
# ---------------------------------------------------------------------------------------------
if [ -f homespool-images.tar.gz ]; then
    echo "homespool: staged image tarball found, loading it"
    docker load -i homespool-images.tar.gz
    rm -f homespool-images.tar.gz
fi

# ---------------------------------------------------------------------------------------------
# .env, written once, from what only this machine can know.
#
# PRINTER_HOST is the setting with no sensible default and the one that hurts when wrong: it is
# written into every provisioning bundle and covered by the printer certificate, which is minted on
# first start and then frozen. An image cannot know it. A *booted Pi* can - it is this machine's own
# address on the LAN - which is what lets a test image skip the wizard entirely.
#
# The sharp edge is DHCP. The certificate names the address it found today; if the lease moves, the
# certificate stops covering the machine and needs a reissue (Admin -> Printer certificate). Give
# this board a static lease before enrolling anything you care about.
# ---------------------------------------------------------------------------------------------
if [ ! -f .env ]; then
    # Preferred: the source address the kernel would use to reach the wider world, which on a machine
    # with several interfaces picks the one that actually carries traffic.
    lan_ip="$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.*src \([0-9.]*\).*/\1/p')"

    # Fallback, and not a rare one: `route get` needs a default route, so a LAN with no gateway - or
    # one whose gateway is down - answers "Network is unreachable" and yields nothing. A print server
    # on an isolated network is an entirely ordinary deployment, and the first version of this looped
    # forever on a board that was reachable over SSH the whole time. Asking "what is my address" is
    # the question actually being asked; "how do I reach 1.1.1.1" was a proxy for it.
    #
    # Fallback, using hostname(1) from the base rather than iproute2, so this still answers if the
    # package is ever dropped again - which is how the original version failed silently on every
    # card built.
    #
    # The interface filter is load-bearing. By the time this runs docker is up, and docker0 and the
    # compose bridge both carry globally-scoped RFC1918 addresses - so an unfiltered pick can hand
    # PRINTER_HOST something like 172.17.0.1, which no printer can reach, and which the certificate
    # is then minted against and frozen. Failing loudly beats configuring the one address guaranteed
    # to be wrong.
    if [ -z "$lan_ip" ]; then
        lan_ip="$(hostname -I 2>/dev/null | tr ' ' '\n' \
                  | grep -E '^[0-9]+\.' \
                  | grep -vE '^(127\.|172\.1[6-9]\.|172\.2[0-9]\.|172\.3[01]\.)' \
                  | head -1)"
    fi

    if [ -z "$lan_ip" ]; then
        # No route means no address worth baking into a certificate, so there is nothing useful to
        # do yet - but this must FAIL rather than succeed quietly.
        #
        # Exiting 0 here is a bug this image actually shipped: combined with RemainAfterExit=yes,
        # systemd recorded the unit as succeeded, and plugging in an ethernet cable five minutes
        # later started nothing. The board sat there with a working network and no stack until it
        # was power-cycled. Failing instead lets Restart=on-failure keep trying, which turns
        # "plugged the cable in afterwards" into the normal, self-healing case.
        echo "homespool: no LAN address yet - will retry" >&2
        exit 1
    fi

    echo "homespool: writing .env with PRINTER_HOST=$lan_ip"
    cp .env.example .env
    sed -i "s|^PRINTER_HOST=.*|PRINTER_HOST=$lan_ip|" .env
    sed -i "s|^USER_HOST=.*|USER_HOST=$(hostname).local|" .env
fi

# ---------------------------------------------------------------------------------------------
# And up. `up -d` is idempotent, so this is also the ordinary every-boot path - it reconciles
# whatever is running against the compose file rather than assuming a clean start.
# ---------------------------------------------------------------------------------------------
#
# --no-build is load-bearing. compose.yaml still carries a `build:` section for developers, and its
# context is the repository - which is not on this card. Without this flag a missing image sends
# compose looking for a source tree that was never shipped, and it fails describing a build error
# rather than the actual fault, which is that docker load did not leave what it should have.
echo "homespool: bringing the stack up"
docker compose up -d --no-build
