# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY Analytika/Analytika.csproj ./Analytika/
RUN dotnet restore ./Analytika/Analytika.csproj
COPY Analytika/ ./Analytika/
RUN dotnet publish ./Analytika/Analytika.csproj \
    -c Release \
    -r linux-x64 \
    --no-self-contained \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
RUN mkdir -p /app/data /app/wwwroot/portal-downloads /app/wwwroot/reports \
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
ENTRYPOINT ["dotnet", "Analytika.dll"]
