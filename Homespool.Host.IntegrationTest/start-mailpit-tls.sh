#!/usr/bin/env bash
# Starts a local Mailpit container with STARTTLS enabled, for testing
# SmtpEmailSender's UseImplicitTls=false / DisableTls=false path (the default
# production configuration) against a real SMTP STARTTLS handshake.
#
# Mailpit offers STARTTLS whenever it's given a certificate and key via
# MP_SMTP_TLS_CERT / MP_SMTP_TLS_KEY - it does not generate one itself. Cert
# generation lives in generate-test-ca.sh (a throwaway CA plus a leaf it
# signs), shared with anything else that wants a locally-trusted TLS server
# certificate without touching the OS trust store.
#
# MP_SMTP_REQUIRE_STARTTLS is deliberately NOT set: STARTTLS is offered but not
# required, so SmtpEmailSenderMailpitTests's existing DisableTls=true
# (SecureSocketOptions.None) path keeps working alongside it.
#
# The leaf certificate is signed by a throwaway CA, not chained to anything in
# the OS trust store, so MailKit's default certificate validation still
# rejects it. SmtpEmailSenderStartTlsMailpitTests validates against the CA
# directly (CustomCaSmtpTransportFactory, X509Chain + CustomTrustStore)
# instead - see generate-test-ca.sh's ca-cert.pem, which that factory loads
# and trusts explicitly, only for the lifetime of the test process.
#
# Usage: ./start-mailpit-tls.sh

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cert_dir="$script_dir/.mailpit-tls"
container_name="mailpit"

"$script_dir/generate-test-ca.sh" "$cert_dir" localhost

if docker ps -a --format '{{.Names}}' | grep -qx "$container_name"; then
    echo "Removing existing '$container_name' container..."
    docker rm -f "$container_name" >/dev/null
fi

echo "Starting Mailpit with STARTTLS enabled on port 1025 (web UI on 8025)..."
docker run -d \
    --name "$container_name" \
    -p 1025:1025 \
    -p 8025:8025 \
    -v "$cert_dir:/certs:ro" \
    -e MP_SMTP_TLS_CERT=/certs/cert.pem \
    -e MP_SMTP_TLS_KEY=/certs/key.pem \
    axllent/mailpit >/dev/null

# Wait for the port to answer before claiming it is up. `docker run -d` returns as soon as the
# container is created, not when Mailpit is listening - which works by luck on a warm laptop and is a
# race on a cold CI runner, where it would surface as an occasional failure in a test that has nothing
# to do with whatever was being changed.
echo "Waiting for SMTP on localhost:1025..."

for attempt in $(seq 1 50); do
    # bash's own /dev/tcp rather than nc, which is not on every CI image and whose flags differ
    # between the BSD and OpenBSD builds.
    if (exec 3<>/dev/tcp/localhost/1025) 2>/dev/null; then
        echo "Mailpit is up: SMTP on localhost:1025 (STARTTLS available), web UI at http://localhost:8025"
        exit 0
    fi

    sleep 0.2
done

echo "Mailpit did not start listening on 1025 within 10 seconds. Container log follows:" >&2
docker logs "$container_name" >&2
exit 1
