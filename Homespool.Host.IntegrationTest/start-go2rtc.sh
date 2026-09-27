#!/usr/bin/env bash
# Starts the camera sidecar - Homespool's own go2rtc image - as a throwaway, for the contract tests
# that hold FakeGo2Rtc to what the real one does.
#
# Run the way compose.yaml runs it: three -c sources in the same order, the API line read out of
# compose.yaml itself so its allow_paths cannot drift from what a deployment gets, and the password
# as a file under CREDENTIALS_DIRECTORY rather than on the command line.
#
# :latest, pulled every time, rather than a pinned digest. For somebody else's image a change
# underneath the suite is noise; for this one it is the thing under test, and :latest is what a
# deployment runs. `docker run` never re-pulls a tag it already has, so the pull is explicit, and
# the digest is printed so a red run can be tied to an image.
#
# GO2RTC_IMAGE overrides the image, and an override is used as it is, without a pull - that is how a
# locally built one (`docker compose build go2rtc`) is tested before it is published.
#
# Usage: ./start-go2rtc.sh

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose="$script_dir/../compose.yaml"
container_name="homespool-go2rtc-contract"
state_dir="${TMPDIR:-/tmp}/homespool-go2rtc-contract"

# Must match Go2RtcFixture.cs.
username="homespool"
password="contract-sidecar-password" # betterleaks:allow - the credential of a sidecar that lives only for a test run
api_port=11984
rtsp_port=18554

if [ -n "${GO2RTC_IMAGE:-}" ]; then
    image="$GO2RTC_IMAGE"
else
    image="ghcr.io/henrikottesorensen/homespool-go2rtc:latest"
    echo "Pulling $image..."
    docker pull --quiet "$image" >/dev/null
fi

# The uncommented line carrying allow_paths - the developer's alternative below it in compose.yaml is
# commented out and carries none. Compose's own substitutions are resolved the way a deployment with
# a credential resolves them.
api_line="$(grep -v '^[[:space:]]*#' "$compose" | grep -o "'{\"api\":{.*allow_paths[^']*'" | head -n 1 | tr -d "'")"

if [ -z "$api_line" ]; then
    echo "No go2rtc api line with allow_paths found in $compose." >&2
    exit 1
fi

api_line="${api_line//'${GO2RTC_USERNAME:+:1984}'/:1984}"
api_line="${api_line//'${GO2RTC_USERNAME:-}'/$username}"

if docker ps -a --format '{{.Names}}' | grep -qx "$container_name"; then
    echo "Removing existing '$container_name' container..."
    docker rm -f "$container_name" >/dev/null
fi

rm -rf "$state_dir"
mkdir -p "$state_dir/config" "$state_dir/secrets"

# Not the `streams: {}` setup-env.sh seeds: go2rtc refuses every stream PUT into a file reading that,
# and this suite is about the API, not that seed. A non-empty block mapping with no streams key is
# accepted by both the startup PATCH and a PUT.
printf 'webrtc:\n  candidates: []\n' > "$state_dir/config/go2rtc.yaml"

printf '%s' "$password" > "$state_dir/secrets/GO2RTC_PASSWORD"

# go2rtc's own placeholder, resolved against CREDENTIALS_DIRECTORY - the file compose.yaml's
# go2rtc-api-credential config holds, with compose's $$ escape undone.
printf 'api:\n  password: "${GO2RTC_PASSWORD:}"\n' > "$state_dir/api-credential.yaml"

# The sidecar runs as 65532 and rewrites its configuration on every stream it is given, so the
# directory and the file must be writable by an identity this machine has never heard of.
chmod 0777 "$state_dir/config"
chmod 0666 "$state_dir/config/go2rtc.yaml"
chmod 0755 "$state_dir/secrets"
chmod 0444 "$state_dir/secrets/GO2RTC_PASSWORD" "$state_dir/api-credential.yaml"

echo "Starting go2rtc on ports $api_port (API) and $rtsp_port (RTSP)..."
docker run -d \
    --name "$container_name" \
    -p "127.0.0.1:$api_port:1984" \
    -p "127.0.0.1:$rtsp_port:8554" \
    -e CREDENTIALS_DIRECTORY=/run/secrets \
    -v "$state_dir/config:/config" \
    -v "$state_dir/secrets:/run/secrets:ro" \
    -v "$state_dir/api-credential.yaml:/run/go2rtc/api-credential.yaml:ro" \
    "$image" \
    go2rtc -c /config/go2rtc.yaml -c "$api_line" -c /run/go2rtc/api-credential.yaml >/dev/null

# Wait for an authenticated answer, not merely an open port: the credential is read from the third
# source, and a sidecar answering 401 is not one the tests can use.
echo "Waiting for the API on localhost:$api_port..."

for attempt in $(seq 1 100); do
    if curl -fsS -u "$username:$password" "http://localhost:$api_port/api/streams" >/dev/null 2>&1; then
        digest="$(docker image inspect --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}' "$image")"
        echo "go2rtc is up: $digest"
        exit 0
    fi

    sleep 0.2
done

echo "go2rtc did not answer within 20 seconds. Container log follows:" >&2
docker logs "$container_name" >&2
exit 1
