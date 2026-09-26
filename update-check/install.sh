#!/bin/sh
# Install the daily check for newer Homespool images on a machine running the stack.
#
#   sudo ./update-check/install.sh [compose-project]
#
# The project is the name compose gives the stack - the deployment directory's name unless .env sets
# COMPOSE_PROJECT_NAME - and defaults to homespool, which is what /opt/homespool gets. Idempotent:
# run it again to repair an installation or change the project.
#
# The check reports only. It reads the running containers and the registry and writes a report; it
# never pulls, restarts or reads a compose file. That is also why this, unlike acme/install.sh, needs
# no check of who can write the deployment directory: nothing there decides what the root timer
# runs. The script is copied out of the checkout into /usr/local/sbin, root's, so the checkout's
# ownership stops mattering once this has run.
#
# The images' registry must allow anonymous reads, as GHCR's public packages do: the timer runs as
# root with no login, deliberately. See homespool-update-check.sh.
set -eu

PROJECT="${1:-homespool}"
SBIN=/usr/local/sbin
UNITS=/etc/systemd/system
here="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
    echo "This installs a systemd timer; run it with sudo." >&2
    exit 1
fi

# The project goes into the unit through sed, so only what compose itself allows in a project name.
case "$PROJECT" in
    "" | *[!a-z0-9_-]*)
        echo "'$PROJECT' is not a compose project name: lowercase letters, digits, - and _ only." >&2
        exit 1
        ;;
esac

missing=''
for tool in docker curl jq; do
    command -v "$tool" >/dev/null 2>&1 || missing="$missing $tool"
done
if [ -z "$missing" ] && ! docker buildx version >/dev/null 2>&1; then
    missing=" docker-buildx-plugin"
fi
if [ -n "$missing" ]; then
    echo "The check needs:$missing - install that first, or it would fail every day." >&2
    exit 1
fi

install -m 0755 "$here/homespool-update-check.sh" "$SBIN/homespool-update-check"
sed "s/@PROJECT@/$PROJECT/" "$here/homespool-update-check.service" > "$UNITS/homespool-update-check.service"
chmod 0644 "$UNITS/homespool-update-check.service"
install -m 0644 "$here/homespool-update-check.timer" "$UNITS/homespool-update-check.timer"

systemctl daemon-reload
systemctl enable --now homespool-update-check.timer

echo "Installed for compose project '$PROJECT'. To check now rather than tonight:"
echo "  sudo systemctl start homespool-update-check.service"
echo "  journalctl -u homespool-update-check.service"
echo "  cat /var/lib/homespool/update-check.json"
