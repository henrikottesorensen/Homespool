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

HOSTS="${USER_HOSTS:-localhost}"

# Every name in USER_HOSTS becomes a subject-alternative name, and the first is also the common name.
# The CN is there for anything old enough to still read one; no browser has honoured it since 2017,
# which is why the list below is what actually decides whether the certificate matches.
#
# DNS: or IP:, decided per entry, and this is the one place the two certificates in this deployment
# follow OPPOSITE rules. A browser resolves an IP URL against the iPAddress entries only and ignores
# a name that happens to look like an address. The printer leaf is the other way round - the
# firmware's mbedTLS understands dNSName and nothing else, so an address there has to be spelled as
# a name to match at all. Copying either rule to the other certificate
# produces a handshake failure that names neither.
#
# IFS rather than a pipeline: the loop appends to $SAN and a pipeline would run it in a subshell,
# where the assignments would be discarded and the certificate would come out carrying localhost
# alone. Nothing would report it - the file exists, nginx starts, and only a browser says otherwise.
# localhost and 127.0.0.1 are appended to the list rather than to the result, so that they go through
# the same duplicate check as everything else: the default USER_HOSTS is `localhost`, and naming it
# twice is the one case that would otherwise show up in every stock deployment.
#
# Appended into the variable rather than onto the `for` word, which reads like the same thing and is
# not: field splitting applies only to what an unquoted expansion produced, so a quoted literal
# spliced on there would stay glued to the last name instead of becoming two more entries.
HOSTS="$HOSTS;localhost;127.0.0.1"

NAME=""
SAN=""
SEEN=""
OLD_IFS="$IFS"
IFS=';'
for host in $HOSTS; do
    # A stray space around a semicolon is a typo, not a name. Trimmed rather than refused, because
    # refusing means no certificate at all and therefore no proxy - a punishing answer to a space.
    host="$(echo "$host" | tr -d '[:space:]')"
    [ -n "$host" ] || continue

    # Whole-entry match, which the delimiters are what make it: without them `home.lan` would count
    # `myhome.lan` as already present and quietly drop it.
    case ";$SEEN" in
        *";$host;"*) continue ;;
    esac
    SEEN="$SEEN$host;"

    # The common name is the first name the OPERATOR gave, so the appended pair cannot become it -
    # they are reached only when USER_HOSTS was empty, which is when localhost is the right answer.
    [ -n "$NAME" ] || NAME="$host"

    case "$host" in
        # Deliberately crude: anything made only of digits and dots is an IPv4 address, and nothing
        # else can be. A hostname cannot be, since a top-level label may not be all-numeric.
        *[!0-9.]*) SAN="$SAN,DNS:$host" ;;
        *)         SAN="$SAN,IP:$host" ;;
    esac
done
IFS="$OLD_IFS"

SAN="${SAN#,}"

# RSA 2048, not the ECDSA P-256 the printer certificate must use: nothing on this path has the
# firmware's single-ciphersuite constraint, so the most widely accepted key wins instead.
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -keyout "$KEY" -out "$CERT" \
    -subj "/CN=$NAME" \
    -addext "subjectAltName=$SAN" \
    2>/dev/null

chmod 600 "$KEY"

echo "$0: generated a self-signed certificate for $SAN, valid ten years."
echo "$0: browsers will warn that it is not trusted, because it is not. To use your own instead,"
echo "$0: put homespool.crt and homespool.key in the homespool-proxy-certs volume and restart."
