#!/usr/bin/env bash
# Generates a throwaway CA and a leaf certificate signed by it, for tests that want to validate a
# real certificate chain against a trust anchor they control - without touching the OS trust store.
# Not Mailpit-specific; anything needing a locally-trusted TLS server certificate can use this.
#
# Idempotent: skipped entirely if all four output files already exist. Delete the output directory
# to force regeneration.
#
# Usage: ./generate-test-ca.sh [output-dir] [common-name]
#   output-dir   Defaults to ./.mailpit-tls relative to this script.
#   common-name  Defaults to "localhost". Also set as the leaf certificate's only SAN entry
#                (plus 127.0.0.1), so clients connecting by that name validate cleanly.
#
# Writes, into output-dir:
#   ca-key.pem    CA private key - not needed at connection time, kept for re-signing later
#   ca-cert.pem   CA certificate - the trust anchor a test loads and trusts explicitly
#   key.pem       Leaf (server) private key - what the TLS server is configured with
#   cert.pem      Leaf certificate, signed by the CA - what the TLS server presents

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out_dir="${1:-$script_dir/.mailpit-tls}"
common_name="${2:-localhost}"

ca_key_file="$out_dir/ca-key.pem"
ca_cert_file="$out_dir/ca-cert.pem"
key_file="$out_dir/key.pem"
cert_file="$out_dir/cert.pem"

if [[ -f "$ca_key_file" && -f "$ca_cert_file" && -f "$key_file" && -f "$cert_file" ]]; then
    echo "Using existing CA and leaf certificate in $out_dir"
    exit 0
fi

mkdir -p "$out_dir"

echo "Generating a throwaway CA in $out_dir..."
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -keyout "$ca_key_file" -out "$ca_cert_file" \
    -subj "/CN=Homespool Integration Test CA" \
    -addext "basicConstraints=critical,CA:true" \
    -addext "keyUsage=critical,keyCertSign,cRLSign"

echo "Generating a leaf certificate for '$common_name', signed by that CA..."
csr_file="$(mktemp)"
ext_file="$(mktemp)"
trap 'rm -f "$csr_file" "$ext_file" "$out_dir/ca-cert.srl"' EXIT

openssl req -newkey rsa:2048 -nodes -keyout "$key_file" -out "$csr_file" -subj "/CN=$common_name"

cat > "$ext_file" <<EOF
subjectAltName=DNS:$common_name,IP:127.0.0.1
extendedKeyUsage=serverAuth
EOF

openssl x509 -req -in "$csr_file" -CA "$ca_cert_file" -CAkey "$ca_key_file" -CAcreateserial \
    -out "$cert_file" -days 365 -sha256 -extfile "$ext_file"

echo "Wrote $ca_cert_file (CA), $cert_file and $key_file (leaf, signed by that CA)"
