# syntax=docker/dockerfile:1

# Build stage. The whole repository is the build context because the server pulls in every other project.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on its own layer: changing application code then does not re-download packages.
COPY nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/DailyMusings.Domain/DailyMusings.Domain.csproj src/DailyMusings.Domain/
COPY src/DailyMusings.Contracts/DailyMusings.Contracts.csproj src/DailyMusings.Contracts/
COPY src/DailyMusings.Application/DailyMusings.Application.csproj src/DailyMusings.Application/
COPY src/DailyMusings.Infrastructure/DailyMusings.Infrastructure.csproj src/DailyMusings.Infrastructure/
COPY src/DailyMusings.Admin/DailyMusings.Admin.csproj src/DailyMusings.Admin/
COPY src/DailyMusings.Server/DailyMusings.Server.csproj src/DailyMusings.Server/
RUN dotnet restore src/DailyMusings.Server/DailyMusings.Server.csproj

COPY src/ ./src/

# No --no-restore here, deliberately. The restore layer above is keyed on the project files only, so adding or moving
# a Razor component — which is what makes the framework publish _framework/blazor.web.js — does not invalidate it.
# Publishing against that stale graph silently builds the project without Razor support and drops the script: the
# assertion below caught exactly that. Restoring again costs nothing (the packages are already in the layer) and
# keeps the build honest.
RUN dotnet publish src/DailyMusings.Server/DailyMusings.Server.csproj \
        --configuration Release \
        --output /app

# The admin pages reference _framework/blazor.web.js, and the framework does not publish it unless the host project
# itself contains a Razor component. Without the script the pages render and every button on them silently does
# nothing, which is a defect nobody sees until they try to use the page — so the build fails here instead of shipping
# that. The stylesheet is asserted alongside it for the same reason.
RUN test -f /app/wwwroot/_framework/blazor.web.js \
    && test -f /app/wwwroot/_content/DailyMusings.Admin/admin.css

# Runtime stage.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only here so the compose healthcheck can call /api/system/health. Without it the container has no
# HTTP client at all, and a healthcheck that always "passes" is worse than none.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# The instance's whole state lives under one directory (§15.2), so a single volume is enough to be durable.
# The key ring lives below the separately mounted configuration directory: it signs the admin's auth cookies,
# so it must survive a container recreate but must NOT be included in the ordinary content backup (§10.4).
RUN mkdir -p /var/lib/dailymusings /var/lib/dailymusings-config/keys \
    && chown -R "$APP_UID" /var/lib/dailymusings /var/lib/dailymusings-config

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:18321 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    Storage__RootPath=/var/lib/dailymusings \
    Storage__SecretsPath=/run/secrets \
    Storage__KeyRingPath=/var/lib/dailymusings-config/keys \
    Storage__RuntimeConfigPath=/var/lib/dailymusings-config/runtime.json

EXPOSE 18321

USER $APP_UID

ENTRYPOINT ["dotnet", "DailyMusings.Server.dll"]
