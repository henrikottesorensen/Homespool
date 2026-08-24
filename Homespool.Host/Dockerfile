FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
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
RUN mkdir -p /app/data /app/data/proxy-certificates \
 && chown $APP_UID /app/data /app/data/proxy-certificates
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
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl hwdata sqlite3 \
 && rm -rf /var/lib/apt/lists/*
USER $APP_UID
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
# --use-current-runtime here as well as on the publish below, and it is not decoration: without it
# this layer restores RID-agnostically, publish restores again for the runtime it targets, and the
# cached layer buys nothing.
RUN dotnet restore "Homespool.Host/Homespool.Host.csproj" --use-current-runtime
# Restore the LibMan CLI pinned in .config/dotnet-tools.json (copied first so this layer caches on
# the manifest, not the whole tree).
COPY [".config/dotnet-tools.json", ".config/"]
RUN dotnet tool restore
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

# /health/live, not /health. Docker marks the container unhealthy on failure, and orchestrators that
# act on that (Swarm, autoheal sidecars) restart it - so this must only fail for faults a restart
# fixes. A database rejecting writes is not one: restarting discards everything still buffered.
# Scrape /health for that, from something that alerts rather than restarts.
#
# Deliberately no --location: the app excludes /health from HTTPS redirection, because curl --fail
# treats a 307 as success and would otherwise report healthy without ever reaching the endpoint.
#
# --start-interval is what makes the proxy appear promptly. compose gates it on service_healthy, so
# nothing answers port 80 until this check passes - and with only --interval, a container that became
# ready one second after a poll waited another 30 s to be noticed. On a Raspberry Pi, where the first
# migration already takes minutes, that trailing half-minute of refused connections reads as a broken
# deployment; it was reported as one. Polling every 5 s until the first success costs nothing
# afterwards, because the fast interval applies only during the start period.
#
# --start-period is 120 s rather than 30 s for the same class of machine. It is not a delay: a check
# that succeeds during the start period ends it immediately, so this is free on hardware that boots
# quickly. What it buys is that failures before then do not count towards --retries - and on an SD
# card, 30 s + 3x30 s could expire *during* the first EF migration and have an orchestrator restart
# the container in the middle of it.
HEALTHCHECK --interval=30s --start-interval=5s --timeout=5s --start-period=120s --retries=3 \
    CMD curl --fail --silent --show-error --max-time 4 http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Homespool.Host.dll"]
