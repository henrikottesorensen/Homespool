#!/usr/bin/env bash
# Removes the throwaway camera sidecar start-go2rtc.sh started, and the files it ran on.
#
# Usage: ./stop-go2rtc.sh

set -euo pipefail

container_name="homespool-go2rtc-contract"
run_record="${TMPDIR:-/tmp}/homespool-go2rtc-contract.dir"

if docker ps -a --format '{{.Names}}' | grep -qx "$container_name"; then
    docker rm -f "$container_name" >/dev/null
    echo "Removed '$container_name'."
else
    echo "No '$container_name' container to remove."
fi

if [ -f "$run_record" ]; then
    rm -rf "$(cat "$run_record")"
    rm -f "$run_record"
fi
