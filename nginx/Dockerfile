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
USER root
RUN mkdir -p /etc/nginx/certs && chown 101:101 /etc/nginx/certs && chmod 700 /etc/nginx/certs

# The dynamic modules the base image installs - geoip, image-filter, njs, xslt - and curl go, because
# nothing here uses them: no configuration has a load_module, and neither the base image's entrypoint
# nor any hook below calls curl. They are not inert on disk. Their libraries - libheif, aom, gd,
# libxml2, libssh2, gnutls, krb5 - account for more than half of the fixable vulnerabilities a scanner
# reports against this image, and every one of those findings costs somebody the time to establish that nginx
# never runs it.
#
# The modules are found rather than named, so one the base image adds later goes too. The image does
# not get smaller - the files stay in the base layer underneath - but they are absent from the running
# container, which is what an attacker and a scanner both see. No apt-get update: removing needs no
# package lists, so this still builds offline.
#
# The loop is the guard. apt-get purge also removes whatever depends on what it removes, without
# asking, so a base image that one day makes a tool the entrypoint needs depend on curl fails this
# build instead of shipping a proxy that breaks at start-up. The list is what the entrypoint and the
# hooks call. A loop rather than one `command -v` with every name: dash answers 0 for such a list even
# when some of the names are missing.
RUN apt-get purge -y $(dpkg-query -W -f='${Package}\n' 'nginx-module-*') curl \
 && apt-get autoremove -y --purge \
 && for tool in nginx openssl hostname envsubst awk sed find getconf dpkg-query; do \
        command -v "$tool" >/dev/null || { echo "The package purge removed $tool" >&2; exit 1; }; \
    done
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

# Baked in rather than mounted from ./nginx like the configuration files, because this one is served
# precisely when things are going wrong. A bind mount is one more thing that can be absent, and its
# absence would turn "the application is still starting" into a bare 404 from the proxy.
COPY --chown=101:101 --chmod=644 starting.html /usr/share/nginx/html/homespool-starting.html

# The same page in Danish, picked by $hs_starting_suffix in homespool.conf.template. Absent, the
# try_files there falls back to the English one, which is what makes adding a language to the
# configuration safe before the image carrying it has been rebuilt.
COPY --chown=101:101 --chmod=644 starting.da.html /usr/share/nginx/html/homespool-starting.da.html

EXPOSE 8080
EXPOSE 8443
EXPOSE 15443
