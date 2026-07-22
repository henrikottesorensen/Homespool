FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# Create the database directory *before* dropping to the app user. A named volume mounted here
# inherits this ownership; if the path did not exist in the image, Docker would create it owned
# by root and the non-root app could not write, failing on the first migration.
RUN mkdir -p /app/data && chown $APP_UID /app/data
USER $APP_UID
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["PrinterService.Api/PrinterService.Api.csproj", "PrinterService.Api/"]
RUN dotnet restore "PrinterService.Api/PrinterService.Api.csproj"
COPY . .
WORKDIR "/src/PrinterService.Api"
RUN dotnet build "./PrinterService.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PrinterService.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PrinterService.Api.dll"]
