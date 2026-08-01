#!/bin/sh
#
# Generates a self-signed certificate on first start, so that `docker compose up` serves the site
# over TLS with nothing configured and no account anywhere. Replaced by simply putting your own
# certificate in the same volume - nginx does not care where it came from.
#
# The browser will warn: the certificate is signed by nobody. That is the honest state of a
# self-hosted LAN service, and it is a better default than serving credentials in clear while
# waiting for someone to obtain a certificate they may have no way to get.
set -eu

CERT_DIR=/etc/nginx/certs
CERT="$CERT_DIR/homespool.crt"
KEY="$CERT_DIR/homespool.key"

# Kept across container replacement by the named volume, deliberately: regenerating on every start
# would invalidate the exception the operator clicked through in their browser last time, and teach
# them that the warning means nothing.
if [ -s "$CERT" ] && [ -s "$KEY" ]; then
    echo "$0: using the existing certificate in $CERT_DIR"
    exit 0
fi

NAME="${USER_HOST:-localhost}"

# RSA 2048, not the ECDSA P-256 the printer certificate must use: nothing on this path has the
# firmware's single-ciphersuite constraint, so the most widely accepted key wins instead.
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -keyout "$KEY" -out "$CERT" \
    -subj "/CN=$NAME" \
    -addext "subjectAltName=DNS:$NAME,DNS:localhost,IP:127.0.0.1" \
    2>/dev/null

chmod 600 "$KEY"

echo "$0: generated a self-signed certificate for $NAME, valid ten years."
echo "$0: browsers will warn that it is not trusted, because it is not. To use your own instead,"
echo "$0: put homespool.crt and homespool.key in the homespool-proxy-certs volume and restart."
