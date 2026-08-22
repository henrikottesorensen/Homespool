#!/usr/bin/env bash
# Stops and removes the dex container started by start-dex.sh.
#
# Storage is in-memory, so there is nothing to clean up beyond the container itself.
#
# Usage: ./stop-dex.sh

set -euo pipefail

container_name="homespool-dex"

if docker ps -a --format '{{.Names}}' | grep -qx "$container_name"; then
    docker rm -f "$container_name" >/dev/null
    echo "Removed '$container_name'."
else
    echo "No '$container_name' container to remove."
fi
