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

echo "Mailpit is up: SMTP on localhost:1025 (STARTTLS available), web UI at http://localhost:8025"
