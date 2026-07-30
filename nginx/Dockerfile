# The user-facing reverse proxy. Printers do not come through here - they reach Kestrel directly on
# their own listener, and putting a proxy in that path breaks a protocol verified byte-for-byte on
# hardware (see notes/tls-by-default.md, decision 3a).
#
# nginxinc/nginx-unprivileged rather than the mainline image: it runs as uid 101 with no root in the
# picture at all, which is worth more here than the one convenience root buys - binding 80/443
# directly. It listens on 8080/8443 inside the container and Compose publishes the low ports onto
# them. Debian-based rather than Alpine, deliberately: musl's differences are subtle enough to be
# discovered at the wrong moment.
FROM nginxinc/nginx-unprivileged:stable

# Create the certificate directory *before* the volume is mounted over it, owned by the app user. A
# named volume inherits the ownership of the image directory it covers; if the path did not exist,
# Docker would create it owned by root and an unprivileged nginx could not write the self-signed
# certificate it generates on first run. Exactly the trap Homespool.Host/Dockerfile documents for
# /app/data, in a container that has even less recourse.
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
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/25-self-signed-certificate.sh /docker-entrypoint.d/

EXPOSE 8080
EXPOSE 8443
