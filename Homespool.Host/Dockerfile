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
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
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
RUN dotnet restore "Homespool.Host/Homespool.Host.csproj"
# Restore the LibMan CLI pinned in .config/dotnet-tools.json (copied first so this layer caches on
# the manifest, not the whole tree).
COPY [".config/dotnet-tools.json", ".config/"]
RUN dotnet tool restore
COPY . .
WORKDIR "/src/Homespool.Host"
# Fetch the client-side libraries (bootstrap, jquery, ...) into wwwroot/lib per libman.json. They are
# gitignored rather than committed, so they must be materialised here before publish captures the
# static web assets.
RUN dotnet libman restore
RUN dotnet build "./Homespool.Host.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Homespool.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

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
