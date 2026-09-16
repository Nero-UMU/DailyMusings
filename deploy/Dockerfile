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
RUN dotnet publish src/DailyMusings.Server/DailyMusings.Server.csproj \
        --configuration Release \
        --no-restore \
        --output /app

# Runtime stage.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only here so the compose healthcheck can call /api/system/health. Without it the container has no
# HTTP client at all, and a healthcheck that always "passes" is worse than none.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# The instance's whole state lives under one directory (§15.2), so a single volume is enough to be durable.
# The key ring gets its own directory on purpose: it signs the admin's auth cookies, so it must survive a
# container recreate but must NOT be inside the directory that gets backed up (§10.4, decision A.13).
# Creating both here, owned by the runtime user, means fresh named volumes inherit that ownership.
RUN mkdir -p /var/lib/dailymusings /var/lib/dailymusings-keys \
    && chown -R "$APP_UID" /var/lib/dailymusings /var/lib/dailymusings-keys

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    Storage__RootPath=/var/lib/dailymusings \
    Storage__SecretsPath=/run/secrets \
    Storage__KeyRingPath=/var/lib/dailymusings-keys

EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "DailyMusings.Server.dll"]
