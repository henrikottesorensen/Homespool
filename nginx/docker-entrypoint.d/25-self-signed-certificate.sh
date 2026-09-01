#!/bin/sh
#
# Generates a self-signed certificate for every name this deployment answers to, so that
# `docker compose up` serves the site over TLS with nothing configured and no account anywhere.
#
# The browser will warn: the certificate is signed by nobody. That is the honest state of a
# self-hosted LAN service, and it is a better default than serving credentials in clear while
# waiting for someone to obtain a certificate they may have no way to get.
#
# ONE CERTIFICATE PER NAME, not one certificate naming all of them. A single multi-name certificate
# works right up until one name gets a publicly-issued certificate and the others cannot: no
# authority will sign `homespool.lan`, so a deployment reached at both a public name and a LAN name
# needs two certificates and nginx must choose between them per server block. Minting them one to a
# name means the choice is already made by the time nginx starts, and a name whose certificate is
# replaced by a real one affects no other name.
#
# WHAT THIS SCRIPT WILL NOT DO IS OVERWRITE. A name that already has a certificate keeps it,
# whatever its provenance - self-signed from an earlier start, dropped in by hand, or issued by an
# ACME client into certificates/ below. Regenerating on every start would invalidate the exception
# the operator clicked through in their browser last time, and teach them that the warning means
# nothing.
set -eu

CERT_DIR=/etc/nginx/certs

# Where an ACME client writes, and the reason nothing here has to be linked or copied into place:
# 26-user-tls-servers.sh prefers a certificate here over the self-signed one beside it, so obtaining
# a real certificate for a name is entirely a matter of putting it in this directory under that
# name. lego's own `--path` layout puts it here already.
ACME_DIR="$CERT_DIR/certificates"

# The certificate the 8443 default server presents to a request whose Host this deployment does not
# recognise. Deliberately not one of the real ones - see homespool.conf.template, which explains why
# handing a scanner the operator's actual domain is worth one extra key to avoid.
DEFAULT_NAME=default

# Ten years. These are replaced by being replaced, not by expiring, and an expiry warning on a
# certificate nobody trusts anyway is noise that trains people to click through the real one.
DAYS=3650

# The subject common name is limited to 64 characters by the standard, and openssl refuses to build
# a longer one rather than truncating it. No browser has honoured a CN since 2017 - the SAN below is
# what actually decides whether the certificate matches - so a name too long for the field is issued
# with no CN at all rather than not issued.
CN_MAX=64

# Every name, deduplicated, derived by 16-user-server-names.envsh. NOT split from USER_HOSTS here:
# 26-user-tls-servers.sh writes one server block per name from the same list, and a name that gets a
# certificate here but no block there is served by nobody, while a block with no certificate stops
# nginx from starting outright.
NAMES="${USER_TLS_NAMES:-localhost}"

# Issues one self-signed certificate. $1 is the file stem, $2 the subjectAltName argument - empty
# for a certificate that is meant to match nothing.
issue() {
    stem="$1"
    san="$2"
    crt="$CERT_DIR/$stem.crt"
    key="$CERT_DIR/$stem.key"

    # The CN is there for anything old enough to still read one.
    if [ "${#stem}" -le "$CN_MAX" ]; then
        subject="/CN=$stem"
    else
        subject="/"
        echo "$0: $stem is over $CN_MAX characters, issuing with no common name" >&2
    fi

    # RSA 2048, not the ECDSA P-256 the printer certificate must use: nothing on this path has the
    # firmware's single-ciphersuite constraint, so the most widely accepted key wins instead.
    if [ -n "$san" ]; then
        openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
            -keyout "$key" -out "$crt" \
            -subj "$subject" \
            -addext "subjectAltName=$san" \
            2>/dev/null
    else
        # No SAN at all, which is what makes this one unusable as anything but a handshake: every
        # browser has required a matching SAN since 2017, so it cannot accidentally satisfy a name.
        openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
            -keyout "$key" -out "$crt" \
            -subj "$subject" \
            2>/dev/null
    fi

    chmod 600 "$key"
}

# The word list of a `for` is expanded once, before the first iteration, so IFS only has to hold
# across that expansion - the body is free to leave it alone. It is restored immediately after.
OLD_IFS="$IFS"
IFS=';'
set -- $NAMES
IFS="$OLD_IFS"

for host in "$@"; do
    # A name reaches this script from .env and becomes a file path, so it is checked before it is
    # one. Anything outside the hostname character set - a slash most of all - is refused rather
    # than sanitised: a name that cannot be served is a configuration error to report, and quietly
    # rewriting it would serve a certificate for a name the operator did not ask for.
    case "$host" in
        *[!A-Za-z0-9.-]* | .* | '')
            echo "$0: refusing $host - not a usable hostname" >&2
            continue
            ;;
    esac

    # Already has one, from any source. certificates/ is checked first for the same reason
    # 26-user-tls-servers.sh prefers it: a real certificate outranks the placeholder beside it.
    if [ -s "$ACME_DIR/$host.crt" ] && [ -s "$ACME_DIR/$host.key" ]; then
        echo "$0: $host has an issued certificate in ${ACME_DIR##*/}/"
        continue
    fi

    if [ -s "$CERT_DIR/$host.crt" ] && [ -s "$CERT_DIR/$host.key" ]; then
        echo "$0: $host already has a certificate"
        continue
    fi

    # DNS: or IP:, decided per entry, and this is the one place the two certificates in this
    # deployment follow OPPOSITE rules. A browser resolves an IP URL against the iPAddress entries
    # only and ignores a name that happens to look like an address. The printer leaf is the other
    # way round - the firmware's mbedTLS understands dNSName and nothing else, so an address there
    # has to be spelled as a name to match at all. Copying either rule to the other certificate
    # produces a handshake failure that names neither.
    #
    # Deliberately crude: anything made only of digits and dots is an IPv4 address, and nothing else
    # can be. A hostname cannot be, since a top-level label may not be all-numeric.
    case "$host" in
        *[!0-9.]*) issue "$host" "DNS:$host" ;;
        *)         issue "$host" "IP:$host" ;;
    esac

    echo "$0: generated a self-signed certificate for $host, valid ten years."
done

# The default server's, issued last and on the same terms: only if it is missing.
if [ ! -s "$CERT_DIR/$DEFAULT_NAME.crt" ] || [ ! -s "$CERT_DIR/$DEFAULT_NAME.key" ]; then
    issue "$DEFAULT_NAME" ""
    echo "$0: generated a certificate for the default server, which matches no name by design."
fi

echo "$0: browsers will warn about any self-signed certificate above, because it is not trusted."
echo "$0: to use your own for a name, put <name>.crt and <name>.key in the homespool-proxy-certs"
echo "$0: volume - or let an ACME client write into its certificates/ directory - and restart."
