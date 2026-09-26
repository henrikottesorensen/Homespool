# The base image by floating tag, so Microsoft's rebuilds arrive without an edit here, and by digest
# as well when the build supplies one: ./build.sh resolves the tag to whatever the registry serves at
# that moment (tools/base-digest.sh, which reads the tag from this ARG line) and the image is built on
# exactly that, and records it - in the labels at the end for anything outside, and in
# HOMESPOOL_IMAGE_BASE for the application. Without a digest, as a bare `docker compose build` leaves
# it, this builds on the tag and records the tag alone.
ARG HOMESPOOL_BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG HOMESPOOL_BASE_DIGEST=
FROM ${HOMESPOOL_BASE_IMAGE}${HOMESPOOL_BASE_DIGEST:+@${HOMESPOOL_BASE_DIGEST}} AS base
WORKDIR /app
# Create the database directory *before* dropping to the app user. A named volume mounted here
# inherits this ownership; if the path did not exist in the image, Docker would create it owned
# by root and the non-root app could not write, failing on the first migration.
#
# /app/data/proxy-certificates is a SECOND named volume, shared with the proxy, and it needs the same
# treatment for the same reason - which was learned the hard way twice: without this line the app
# starts, migrates, mints the authority, and then dies with UnauthorizedAccessException writing the
# leaf, after the point where the failure still looks like a certificate problem.
#
# It is a separate volume rather than a subdirectory of the one above because the proxy mounts it and
# must never see /app/data/certificates, which holds the authority's private key. See
# CertificateOptions.ProxyDirectory.
RUN mkdir -p /app/data /app/data/proxy-certificates && \
    chown $APP_UID /app/data /app/data/proxy-certificates
# The aspnet base image ships no HTTP client, and HEALTHCHECK needs something to make the request
# with. Installed before the USER switch because apt needs root. If you would rather not carry curl,
# drop this and the HEALTHCHECK below and declare the probe in compose or your orchestrator instead -
# the /health endpoint is the same either way.
#
# hwdata carries /usr/share/misc/usb.ids, which is how an attached camera gets a name somebody
# recognises. udev names a device after the strings it reports and falls back to numeric ids when it
# reports none - a Logitech C910 is one of those, listing itself as 046d_0821 - and this table is
# what turns that back into "Logitech HD Webcam C910". Data only, no binaries, and UsbDeviceNames
# degrades to udev's own name when it is absent, so dropping this costs nicer names and nothing else.
#
# sqlite3 is here for the one thing this image cannot otherwise do to itself: repair the database it
# refuses to start against. tools/carry-enrolment.sh needs sqlite3 and the database, and on an
# appliance the database is inside a root-owned volume - so without this the repair runs in a
# throwaway alpine container as ROOT, and everything it creates beside the database is then owned by
# root. Measured, both ways, rather than assumed: the database itself keeps its ownership because it
# is modified in place, but the backup the tool takes first is root-owned every time, and a -wal left
# behind by an interrupted run would be too - which the app cannot recover, and which reports itself
# as "could not enable write-ahead logging", the same sentence as an unrelated permissions problem.
# With sqlite3 here, the script runs in this image as $APP_UID with the volume already mounted where
# it belongs, and leaves nothing the app cannot read:
#
#   docker compose run --rm --no-deps -v ./carry-enrolment.sh:/ce.sh:ro --entrypoint bash homespool \
#       /ce.sh check /app/data/Homespool.Sqlite /app/data/reference.sqlite
#
# The SCRIPT is deliberately NOT copied in. It is a maintenance tool rather than part of the
# application, it is bind-mounted in the one command that needs it, and keeping it out means a fixed
# script can be run against an image that predates the fix.
#
# The upgrade takes the distribution's fixes that Microsoft has not yet rebuilt this base image with.
# It is only worth anything if it runs: Docker caches this layer on the one beneath it, so a later
# build on an unchanged base would reuse the first build's result - the upgrade, and the three
# packages above with it, frozen at whatever day that was. HOMESPOOL_APT_REFRESH breaks that cache:
# any ARG in scope becomes part of the key of every RUN after it, used or not. ./build.sh sets it to
# the date, so this reruns at most once a day. Empty, as a bare `docker compose build` leaves it, the
# layer caches as it always did.
#
# The whole upgrade, not only the security pocket: the stable release's updates are bug fixes, which
# are wanted too, and selecting a pocket with apt is fiddly. confold keeps any configuration file the
# image already has, and noninteractive stops a changed one waiting on a prompt nobody can answer.
ARG HOMESPOOL_APT_REFRESH=
RUN apt-get update && \
    DEBIAN_FRONTEND=noninteractive apt-get upgrade -y \
        -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold && \
    apt-get install -y --no-install-recommends curl hwdata sqlite3 && \
    rm -rf /var/lib/apt/lists/*
USER $APP_UID
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Node, for the ESLint targets in Homespool.Host.csproj, from NodeSource's apt repository - the SDK
# image is Ubuntu, whose own nodejs is older than ESLint accepts. The major version is the one CI's
# setup-node names. First, so every later layer caches on it; only this stage has Node, and the final
# image copies the published output and nothing else.
#
# The repository's signing key is committed rather than downloaded, so what the build trusts is
# decided in a reviewed commit, and a key replaced at NodeSource's URL fails the build instead of
# being taken on trust. gpg/nodesource.asc is
#
#     6F71 F525 2828 41EE DAF8  51B4 2F59 B5F9 9B1B E0B4   NSolid <nsolid-gpg@nodesource.com>
#
# matched against keyserver.ubuntu.com and keys.openpgp.org, and checked with gpgv against the
# node_24.x InRelease. No expiry. If NodeSource rotates it, apt-get update stops with NO_PUBKEY:
# fetch the new key, check its fingerprint the same way, and replace the file. apt reads an armoured
# key named .asc directly, so gnupg is not needed.
ARG NODE_MAJOR=24
COPY ["gpg/nodesource.asc", "/etc/apt/keyrings/nodesource.asc"]
RUN printf 'Types: deb\nURIs: https://deb.nodesource.com/node_%s.x/\nSuites: nodistro\nComponents: main\nSigned-By: /etc/apt/keyrings/nodesource.asc\n' \
        "$NODE_MAJOR" > /etc/apt/sources.list.d/nodesource.sources && \
    apt-get update && \
    apt-get install -y --no-install-recommends nodejs && \
    rm -rf /var/lib/apt/lists/* && \
    node --version && npm --version
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Directory.Build.props first, and it is not optional: it carries TargetFramework for every project
# in the repository, so without it restore fails with NETSDK1013 - "The TargetFramework value '' was
# not recognized" - a message naming neither this file nor the reason. The referenced projects come
# with it, because a restore that cannot see them skips them with a line that reads like a note
# ("Skipping project ... because it was not found") and then fails somewhere else.
#
# Still only the manifests, so the layer caches on the project files rather than on the whole tree.
COPY ["Directory.Build.props", "./"]
COPY ["Homespool.Host/Homespool.Host.csproj", "Homespool.Host/"]
COPY ["Homespool.Data/Homespool.Data.csproj", "Homespool.Data/"]
COPY ["Homespool.Model/Homespool.Model.csproj", "Homespool.Model/"]
# Directory.Build.props references these from every project, so they count as referenced projects too.
COPY ["Homespool.Analysers/Homespool.Analysers.csproj", "Homespool.Analysers/"]
COPY ["Homespool.Analysers.CodeFixes/Homespool.Analysers.CodeFixes.csproj", "Homespool.Analysers.CodeFixes/"]
# --use-current-runtime here as well as on the publish below, and it is not decoration: without it
# this layer restores RID-agnostically, publish restores again for the runtime it targets, and the
# cached layer buys nothing.
RUN dotnet restore "Homespool.Host/Homespool.Host.csproj" --use-current-runtime
# Restore the LibMan CLI pinned in .config/dotnet-tools.json (copied first so this layer caches on
# the manifest, not the whole tree).
COPY [".config/dotnet-tools.json", ".config/"]
RUN dotnet tool restore
# The ESLint tooling, on its own layer for the same reason. .dockerignore keeps node_modules out of the
# copy below, so this install survives it, and the build's own install step then finds it current.
COPY ["Homespool.Host/package.json", "Homespool.Host/package-lock.json", "Homespool.Host/"]
RUN cd Homespool.Host && npm ci --no-audit --no-fund
COPY . .
WORKDIR "/src/Homespool.Host"
# Fetch the client-side libraries (bootstrap, aspnet-client-validation) into wwwroot/lib per
# libman.json. They are gitignored rather than committed, so they must be materialised here before
# publish captures the static web assets.
RUN dotnet libman restore
# The commit this image is built from, because the image cannot work it out for itself: .dockerignore
# excludes **/.git, so SourceLink - which stamps every local build with the commit during compilation -
# finds no repository in here and omits it. The omission is SILENT, guarded by an MSBuild condition
# rather than a warning, which made the deployed artefact the one build that could not say what it is.
#
# Passed as SourceRevisionId rather than through a build argument on each command because MSBuild reads
# ENVIRONMENT VARIABLES AS PROPERTIES: one line covers both the build below and the publish stage that
# derives from it, with no change to either command line. Empty is a supported value - a source tarball
# has no commit - and then --version reports an unknown one instead of the build failing.
#
# Set here rather than at the top of the stage so the restore layers above still cache on the project
# files. `docker compose build` cannot supply this (compose does no command substitution), which is
# what ./build.sh is for; see tools/gitref.sh.
ARG HOMESPOOL_GITREF=
ENV SourceRevisionId=$HOMESPOOL_GITREF
RUN dotnet build "./Homespool.Host.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# --use-current-runtime is worth 31 MB, measured, and the reason is SQLitePCLRaw. A RID-agnostic
# publish copies NuGet's whole runtimes/ tree into the output - 24 platforms of e_sqlite3, 34 MB,
# including win-x64, osx-arm64, browser-wasm, linux-mips64 and linux-ppc64le - of which this image
# loads exactly one, at 1.5 MB. Naming a runtime keeps that one and flattens it into the output root;
# the tree disappears.
#
# --use-current-runtime rather than -r linux-$TARGETARCH because nothing here cross-compiles: this
# stage runs on the target's own architecture, so the SDK already knows the answer, and asking it
# avoids both a TARGETARCH-to-RID mapping table (docker says amd64, .NET says x64) and a dependency
# on BuildKit populating that argument at all.
#
# --self-contained false is explicit rather than relied upon. It is the default for a RID'd build
# since .NET 6, and if that ever changed the failure would be a silently 70 MB larger image carrying
# its own copy of the runtime, which is the opposite of the point.
#
# UseAppHost=false is unchanged and still correct: no native launcher is produced, and the entrypoint
# stays `dotnet Homespool.Host.dll`.
RUN dotnet publish "./Homespool.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish \
        /p:UseAppHost=false --use-current-runtime --self-contained false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Every label the base image sets, set again, because an image inherits its base's labels: without
# these org.opencontainers.image.version says 24.04, which is Ubuntu's, and anything reading it - a
# registry, a scanner report, an update check - is told about somebody else's image. Blank rather
# than left out where there is nothing true to say: version on any build that is not of a v-tagged
# commit (tools/release-version.sh); created, because Docker's own image metadata carries the build
# time.
#
# HOMESPOOL_IMAGE_BASE is the same fact for the process inside, which cannot read its own labels:
# the tag and, when the build pinned one, the digest it was built on. Here, after the last RUN, so
# neither the ARGs nor the ENV reach a cache key.
ARG HOMESPOOL_GITREF=
ARG HOMESPOOL_VERSION=
ARG HOMESPOOL_BASE_IMAGE
ARG HOMESPOOL_BASE_DIGEST
ENV HOMESPOOL_IMAGE_BASE=${HOMESPOOL_BASE_IMAGE}${HOMESPOOL_BASE_DIGEST:+@${HOMESPOOL_BASE_DIGEST}}
LABEL org.opencontainers.image.title="Homespool" \
      org.opencontainers.image.description="A self-hosted alternative to Prusa Connect." \
      org.opencontainers.image.source="https://github.com/henrikottesorensen/Homespool" \
      org.opencontainers.image.url="https://github.com/henrikottesorensen/Homespool" \
      org.opencontainers.image.revision="${HOMESPOOL_GITREF}" \
      org.opencontainers.image.version="${HOMESPOOL_VERSION}" \
      org.opencontainers.image.created="" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.base.name="${HOMESPOOL_BASE_IMAGE}" \
      org.opencontainers.image.base.digest="${HOMESPOOL_BASE_DIGEST}"

# /health/live, not /health. Docker marks the container unhealthy on failure, and orchestrators that
# act on that (Swarm, autoheal sidecars) restart it - so this must only fail for faults a restart
# fixes. A database rejecting writes is not one: restarting discards everything still buffered.
# Scrape /health for that, from something that alerts rather than restarts. Anonymously it answers
# with the overall status only; the full report needs an administrator's sign-in.
#
# Deliberately no --location: the app excludes /health from HTTPS redirection, because curl --fail
# treats a 307 as success and would otherwise report healthy without ever reaching the endpoint.
#
# --start-interval is what gets the container reported healthy promptly. Nothing in compose waits on
# it - the proxy depends on service_started and serves a holding page while this starts - but
# `docker compose ps`, and any monitor reading the status, does: with only --interval, a container
# that became ready one second after a poll would show as starting for another 30 s. Polling every
# 5 s until the first success costs nothing afterwards, because the fast interval applies only
# during the start period.
#
# --start-period is 120 s rather than 30 s for the same class of machine. It is not a delay: a check
# that succeeds during the start period ends it immediately, so this is free on hardware that boots
# quickly. What it buys is that failures before then do not count towards --retries - and on an SD
# card, 30 s + 3x30 s could expire *during* the first EF migration and have an orchestrator restart
# the container in the middle of it.
HEALTHCHECK --interval=30s --start-interval=5s --timeout=5s --start-period=120s --retries=3 \
    CMD curl --fail --silent --show-error --max-time 4 http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Homespool.Host.dll"]
