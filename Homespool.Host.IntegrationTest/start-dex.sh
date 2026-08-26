#!/usr/bin/env bash
# Starts a local dex container as a throwaway OpenID Connect provider, for testing the
# external-login handler against a real authorisation-code flow rather than a stub.
#
# Configuration is dex.yaml beside this script; read it for why the connector is
# mockCallback and why this runs over plain HTTP.
#
# The image tag is pinned rather than :latest. A provider that changes underneath the
# suite turns a protocol regression and an upstream release into the same red, and the
# whole point of testing against a real one is that its behaviour is somebody else's.
#
# Usage: ./start-dex.sh

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
container_name="homespool-dex"
image="ghcr.io/dexidp/dex:v2.44.0"

if docker ps -a --format '{{.Names}}' | grep -qx "$container_name"; then
    echo "Removing existing '$container_name' container..."
    docker rm -f "$container_name" >/dev/null
fi

echo "Starting dex on port 5556..."
docker run -d \
    --name "$container_name" \
    -p 5556:5556 \
    -v "$script_dir/dex.yaml:/etc/dex/config.docker.yaml:ro" \
    "$image" \
    dex serve /etc/dex/config.docker.yaml >/dev/null

# Wait for discovery to answer, not merely for the port to accept. dex binds its listener
# before it has finished loading storage and connectors, so a connect-only probe returns
# while the first request would still 500 - the same cold-start race start-mailpit-tls.sh
# guards against, one layer further up.
echo "Waiting for discovery on localhost:5556..."

for attempt in $(seq 1 100); do
    if curl -fsS http://localhost:5556/dex/.well-known/openid-configuration >/dev/null 2>&1; then
        echo "dex is up: issuer http://localhost:5556/dex"
        exit 0
    fi

    sleep 0.2
done

echo "dex did not serve discovery within 20 seconds. Container log follows:" >&2
docker logs "$container_name" >&2
exit 1
