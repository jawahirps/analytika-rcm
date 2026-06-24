# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY Analytika/Analytika.csproj ./Analytika/
RUN dotnet restore ./Analytika/Analytika.csproj
COPY Analytika/ ./Analytika/
# Framework-dependent, architecture-portable publish (runs on x64 AND arm64,
# e.g. Oracle Cloud Always Free A1/Ampere). The aspnet base image supplies the runtime.
RUN dotnet publish ./Analytika/Analytika.csproj \
    -c Release \
    --no-self-contained \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
# curl is needed for the container HEALTHCHECK below (not in the base image)
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /app/data /app/wwwroot/portal-downloads /app/wwwroot/reports /app/logs \
    && useradd --create-home --shell /usr/sbin/nologin analytika \
    && chown -R analytika:analytika /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV DB_DIR=/app/data
ENV DOTNET_EnableDiagnostics=0
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV StartupMaintenance__RunDatabaseSetupOnStartup=false
ENV StartupMaintenance__CreateIndexesOnStartup=false
ENV StartupMaintenance__SeedDataOnStartup=false
ENV BackgroundJobs__HangfireServerEnabled=false
ENV BackgroundJobs__HangfireDashboardEnabled=false
ENV BackgroundJobs__RecurringJobsEnabled=false
ENV BackgroundJobs__PendingDownloads__HostedServiceEnabled=false

USER analytika

EXPOSE 8080
# Report container health via the app's anonymous /healthz endpoint (DB + sync checks).
# Applies to every deployment that runs this image (incl. the bix host, which pulls it).
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Analytika.dll"]
