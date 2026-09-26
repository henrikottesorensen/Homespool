# The reverse proxy, in front of every listener the application has: people on one port with a
# certificate of the operator's choosing, printers on another with the leaf the application mints
# from its own authority.
#
# The printer half is fussier than an ordinary reverse proxy and is the reason this image exists
# rather than any nginx: it has to present exactly one certificate with no chain, offer one
# ciphersuite, buffer nothing, and bound its TLS records to the kilobyte the firmware can hold. See
# homespool-printer.conf before substituting anything for it.
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
#
# /etc/nginx/templates is made here for a different reason: the base image has none, and a directory a
# COPY creates takes that COPY's --chmod. At 644 it cannot be entered, and the entrypoint exits on
# "Permission denied" reading the first template, before nginx ever starts.
USER root
RUN mkdir -p /etc/nginx/certs /etc/nginx/templates \
    && chown 101:101 /etc/nginx/certs && chmod 700 /etc/nginx/certs
USER 101

# Runs before nginx starts, from the entrypoint's own hook directory. openssl is already in this
# image, so generating the certificate needs nothing installed.
#
# --chmod, not just the file's own mode: the entrypoint skips a hook that is not executable with a
# single "Ignoring ..., not executable" line among a dozen others, and then nginx dies on the missing
# certificate several lines later - so the cause and the symptom do not look related. Stating the
# mode here also survives a checkout that did not preserve it.
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/20-printer-listener.sh /docker-entrypoint.d/
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/21-legacy-printer-listener.sh /docker-entrypoint.d/
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/25-self-signed-certificate.sh /docker-entrypoint.d/

# 26 after 25, and the numbers are load-bearing: 25 mints a certificate for every name that has
# none, and 26 writes the server block that points at it. Reversed, every block would name a
# certificate that did not exist yet and nginx would refuse to start.
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/26-user-tls-servers.sh /docker-entrypoint.d/

# Sourced rather than launched - .envsh is the entrypoint's convention for a hook that has to set
# environment for the scripts after it, here REDIRECT_PORT_SUFFIX and USER_SERVER_NAMES before
# envsubst renders the template. The exec bit is still required: the entrypoint checks -x before
# sourcing too, and skips a non-executable file with the same easy-to-miss one-liner.
#
# Each hook is copied by name rather than the directory wholesale, so adding one here is a step that
# cannot be skipped - a new hook that never reaches the image is a feature that silently does not
# run, and the template it was meant to feed renders with an empty variable instead.
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/15-redirect-port-suffix.envsh /docker-entrypoint.d/
COPY --chown=101:101 --chmod=755 docker-entrypoint.d/16-user-server-names.envsh /docker-entrypoint.d/

# The page served while the application is starting, which is precisely when things are going
# wrong - so it has nothing to depend on but the image.
COPY --chown=101:101 --chmod=644 starting.html /usr/share/nginx/html/homespool-starting.html

# The same page in Danish, picked by $hs_starting_suffix in homespool.conf.template. Absent, the
# try_files there falls back to the English one.
COPY --chown=101:101 --chmod=644 starting.da.html /usr/share/nginx/html/homespool-starting.da.html

# THE CONFIGURATION, ALL OF IT. Nothing under ./nginx is mounted into the container, so the image is
# the whole proxy: one pulled from a registry runs as it was built, no file can be missing from a
# deployment (Docker invents a missing bind-mount source as an empty directory, and nginx then dies
# reading a directory where it wanted a file), and no line can run ahead of the nginx and OpenSSL it
# was written for. What an operator chooses lives in .env; changing a file here is
# `docker compose up -d --build proxy`.
#
# Every file by name, like the hooks above and for the same reason: tests/proxy-image.test.sh fails
# on a file in this directory that no COPY names, which would otherwise be a listener or a setting
# that silently never ships.
#
# Owned by root and not writable by uid 101. conf.d itself stays writable - the entrypoint renders
# the templates into it and the hooks install listeners there.
#
# Templates go to /etc/nginx/templates, which the base image's 20-envsubst-on-templates.sh renders
# into conf.d at start, substituting only what NGINX_ENVSUBST_FILTER in compose.yaml names. The public
# Host map sorts first, so it exists before any server block reads it.
COPY --chmod=644 homespool.conf.template /etc/nginx/templates/default.conf.template
COPY --chmod=644 homespool-public-host.conf.template /etc/nginx/templates/00-public-host.conf.template

# Straight into conf.d, served unconditionally. The access log format sorts first: a format is looked
# up when an access_log naming it is parsed, and conf.d is included in sorted order. The transfer
# listener has no certificate to wait for, so nothing decides whether it is served.
COPY --chmod=644 homespool-access-log.conf /etc/nginx/conf.d/00-access-log.conf
COPY --chmod=644 homespool-http.conf /etc/nginx/conf.d/homespool-http.conf
COPY --chmod=644 homespool-transfer.conf /etc/nginx/conf.d/homespool-transfer.conf

# Beside conf.d, NOT in it. The first three are fragments that server blocks include, and nginx
# refuses a `location` at the top level. The printer blocks are installed by their hooks, and only on
# a condition: 20-printer-listener.sh once there is a printer leaf to serve - unconditional, nginx
# would refuse to start on a stack with printer TLS off - and 21-legacy-printer-listener.sh only when
# LEGACY_PRINTER_PORT asks for a plaintext listener nobody should get by default. That is the same
# variable that opens the application's listener, so the block and the socket behind it cannot
# disagree.
COPY --chmod=644 homespool-proxy.conf homespool-user-tls.conf homespool-health-access.conf /etc/nginx/
COPY --chmod=644 homespool-printer.conf homespool-legacy-printer.conf /etc/nginx/

EXPOSE 8080
EXPOSE 8443
EXPOSE 15443
