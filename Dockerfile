# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Analytika/Analytika.csproj ./Analytika/
RUN dotnet restore ./Analytika/Analytika.csproj
COPY Analytika/ ./Analytika/
RUN dotnet publish ./Analytika/Analytika.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Data directory for SQLite (mount a volume here)
RUN mkdir -p /app/data && mkdir -p /app/wwwroot/portal-downloads

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080
ENTRYPOINT ["dotnet", "Analytika.dll"]
