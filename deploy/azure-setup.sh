#!/usr/bin/env bash
# Azure App Service one-click setup for Analytika RCM
# Usage: ./deploy/azure-setup.sh [resource-group] [app-name] [location]
#
# Prerequisites:
#   - Azure CLI installed and logged in (az login)
#   - GitHub repo with GHCR image published (release.yml)
#
# This creates:
#   1. Resource group
#   2. Azure Database for PostgreSQL Flexible Server (B_Standard_B1ms — $12/mo)
#   3. App Service Plan (B1 Linux — $13/mo)
#   4. Web App for Containers pulling from GHCR
#   5. Configures all env vars and health checks

set -euo pipefail

RG="${1:-analytika-rcm-rg}"
APP="${2:-analytika-rcm}"
LOCATION="${3:-eastus}"
PG_SERVER="${APP}-pg"
PG_ADMIN="analytikaadmin"
PG_PASS="$(openssl rand -base64 24 | tr -d '/+=' | head -c 20)Aa1!"
PG_DB="analytika"
PLAN="${APP}-plan"
GHCR_IMAGE="ghcr.io/ghafbi/analytika-rcm:latest"

echo "=== Analytika RCM — Azure App Service Setup ==="
echo "Resource Group : $RG"
echo "App Name       : $APP"
echo "Location       : $LOCATION"
echo "PostgreSQL     : $PG_SERVER"
echo ""

# 1. Resource group
echo "[1/5] Creating resource group..."
az group create --name "$RG" --location "$LOCATION" -o none

# 2. PostgreSQL Flexible Server
echo "[2/5] Creating PostgreSQL Flexible Server (this takes ~3 min)..."
az postgres flexible-server create \
  --resource-group "$RG" \
  --name "$PG_SERVER" \
  --location "$LOCATION" \
  --admin-user "$PG_ADMIN" \
  --admin-password "$PG_PASS" \
  --database-name "$PG_DB" \
  --sku-name Standard_B1ms \
  --tier Burstable \
  --storage-size 32 \
  --version 16 \
  --public-access 0.0.0.0 \
  -o none

PG_HOST="${PG_SERVER}.postgres.database.azure.com"
DATABASE_URL="Host=${PG_HOST};Port=5432;Database=${PG_DB};Username=${PG_ADMIN};Password=${PG_PASS};SslMode=Require;"

# 3. App Service Plan
echo "[3/5] Creating App Service Plan (Linux B1)..."
az appservice plan create \
  --resource-group "$RG" \
  --name "$PLAN" \
  --is-linux \
  --sku B1 \
  -o none

# 4. Web App for Containers
echo "[4/5] Creating Web App..."
az webapp create \
  --resource-group "$RG" \
  --plan "$PLAN" \
  --name "$APP" \
  --container-image-name "$GHCR_IMAGE" \
  -o none

# 5. Configure environment variables
echo "[5/5] Configuring app settings..."
az webapp config appsettings set \
  --resource-group "$RG" \
  --name "$APP" \
  --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    Database__Provider=postgres \
    "ConnectionStrings__Postgres=${DATABASE_URL}" \
    StartupMaintenance__RunDatabaseSetupOnStartup=true \
    StartupMaintenance__CreateIndexesOnStartup=true \
    StartupMaintenance__SeedDataOnStartup=true \
    BackgroundJobs__HangfireServerEnabled=true \
    BackgroundJobs__RecurringJobsEnabled=true \
    BackgroundJobs__HangfireDashboardEnabled=false \
    Logging__JsonConsole=true \
    WEBSITES_PORT=8080 \
  -o none

# Health check
az webapp config set \
  --resource-group "$RG" \
  --name "$APP" \
  --generic-configurations '{"healthCheckPath": "/health"}' \
  -o none

echo ""
echo "=== Setup Complete ==="
echo ""
echo "App URL        : https://${APP}.azurewebsites.net"
echo "PostgreSQL Host: $PG_HOST"
echo "PG Admin       : $PG_ADMIN"
echo "PG Password    : $PG_PASS  (save this!)"
echo ""
echo "Next steps:"
echo "  1. Add AZURE_CREDENTIALS secret to your GitHub repo (service principal JSON)"
echo "  2. Set repo variable AZURE_DEPLOY_ENABLED=true"
echo "  3. Set repo variable AZURE_WEBAPP_NAME=${APP}"
echo "  4. Push to main — the release workflow auto-deploys to Azure"
echo ""
echo "To create the service principal for GitHub Actions:"
echo "  az ad sp create-for-rbac --name '${APP}-deploy' \\"
echo "    --role contributor \\"
echo "    --scopes /subscriptions/\$(az account show --query id -o tsv)/resourceGroups/${RG} \\"
echo "    --json-auth"
