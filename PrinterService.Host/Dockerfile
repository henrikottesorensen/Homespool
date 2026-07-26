FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# Create the database directory *before* dropping to the app user. A named volume mounted here
# inherits this ownership; if the path did not exist in the image, Docker would create it owned
# by root and the non-root app could not write, failing on the first migration.
RUN mkdir -p /app/data && chown $APP_UID /app/data
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
COPY ["PrinterService.Host/PrinterService.Host.csproj", "PrinterService.Host/"]
RUN dotnet restore "PrinterService.Host/PrinterService.Host.csproj"
# Restore the LibMan CLI pinned in .config/dotnet-tools.json (copied first so this layer caches on
# the manifest, not the whole tree).
COPY [".config/dotnet-tools.json", ".config/"]
RUN dotnet tool restore
COPY . .
WORKDIR "/src/PrinterService.Host"
# Fetch the client-side libraries (bootstrap, jquery, ...) into wwwroot/lib per libman.json. They are
# gitignored rather than committed, so they must be materialised here before publish captures the
# static web assets.
RUN dotnet libman restore
RUN dotnet build "./PrinterService.Host.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PrinterService.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

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
# start-period covers migrations on first boot.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error --max-time 4 http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "PrinterService.Host.dll"]
