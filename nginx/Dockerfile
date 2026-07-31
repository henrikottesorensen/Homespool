# The reverse proxy, in front of every listener the application has: people on one port with a
# certificate of the operator's choosing, printers on another with the leaf the application mints
# from its own authority.
#
# Printers used to be excluded from here, and the comment that said so was confident and specific:
# "putting a proxy in that path breaks a protocol verified byte-for-byte on hardware". What that
# verification predated was TLS. Kestrel terminating it breaks the same protocol worse - SslStream
# ignores the max_fragment_length a printer negotiates, so records arrive 16x larger than the
# kilobyte of plaintext the firmware can hold, and file transfers fail. OpenSSL honours the
# extension, nginx is OpenSSL, so the proxy is now the thing that makes the printer path work rather
# than the thing that would break it. See notes/tls-by-default.md, "Decision 3a's premise has
# shifted", and homespool-printer.conf.
#
# nginxinc/nginx-unprivileged rather than the mainline image: it runs as uid 101 with no root in the
# picture at all, which is worth more here than the one convenience root buys - binding 80/443
# directly. It listens on 8080/8443/15443 inside the container and Compose publishes the low ports
# onto them. Debian-based rather than Alpine, deliberately: musl's differences are subtle enough to
# be discovered at the wrong moment.
FROM nginxinc/nginx-unprivileged:stable

# Create the certificate directory *before* the volume is mounted over it, owned by the app user. A
# named volume inherits the ownership of the image directory it covers; if the path did not exist,
# Docker would create it owned by root and an unprivileged nginx could not write the self-signed
# certificate it generates on first run. Exactly the trap Homespool.Host/Dockerfile documents for
# /app/data, in a container that has even less recourse.
#
# /etc/nginx/printer-certs is the other half of that story and needs the opposite treatment: it holds
# the application's leaf, is mounted read-only from the application's own volume, and nothing in here
# ever writes to it.
USER root
RUN mkdir -p /etc/nginx/certs && chown 101:101 /etc/nginx/certs && chmod 700 /etc/nginx/certs
USER 101

# Runs before nginx starts, from the entrypoint's own hook directory. openssl is already in this
# image, so generating the certificate needs nothing installed.
#
# --chmod, not just the file's own mode: the entrypoint skips a hook that is not executable with a
# single "Ignoring ..., not executable" line among a dozen others, and then nginx dies on the missing
# certificate several lines later - so the cause and the symptom do not look related. Stating the
# mode here also survives a checkout that did not preserve it.
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/20-printer-listener.sh /docker-entrypoint.d/
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/25-self-signed-certificate.sh /docker-entrypoint.d/

EXPOSE 8080
EXPOSE 8443
EXPOSE 15443
