#!/usr/bin/env bash
# Stops and removes the Mailpit container started by start-mailpit-tls.sh.
# Leaves the generated self-signed certificate in .mailpit-tls/ alone, so the
# next start-mailpit-tls.sh run doesn't need to regenerate it.
#
# Usage: ./stop-mailpit-tls.sh

set -euo pipefail

container_name="mailpit"

if docker ps -a --format '{{.Names}}' | grep -qx "$container_name"; then
    echo "Stopping and removing '$container_name'..."
    docker rm -f "$container_name" >/dev/null
    echo "Done."
else
    echo "No '$container_name' container found; nothing to do."
fi
