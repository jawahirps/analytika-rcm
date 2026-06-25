# Analytika RCM — Helm chart

Deploys Analytika RCM to a managed Kubernetes cluster (AKS / EKS / GKE) as two tiers
built from the **same container image**:

- **web** — horizontally scalable, stateless, serves HTTP. All background-job flags off.
- **worker** — single replica. Runs Hangfire server, recurring jobs, and the
  pending-downloads hosted service.

A pre-install/pre-upgrade **migration job** brings the Postgres schema current
(`dotnet Analytika.dll --migrate`) before the pods roll, so replicas never race on
`Database.Migrate()`.

## Architecture decisions

| Concern        | Choice |
|----------------|--------|
| Database       | **Managed/external Postgres**. App auto-selects Postgres from `DATABASE_URL` (`Modules/DatabaseConfig.cs`). |
| Manifests      | **Helm chart**. |
| Shared state   | **One RWX PVC** mounted by web + worker for the DataProtection keyring (`/app/data`), reports, and portal-downloads. |
| Migrations     | Helm hook job, with `StartupMaintenance__MigrateOnStartup=false` on the pods. |
| TLS            | ingress + cert-manager. The app already emits HSTS/CSP. |

### Why the RWX volume matters
The DataProtection keyring under `/app/data/dataprotection-keys` **encrypts portal
credentials at rest** (`Program.cs`). Every web/worker pod must share the same keyring,
or credentials saved by one pod can't be decrypted by another. Hence `ReadWriteMany`.

## Prerequisites

- A managed cluster with an **RWX storage class** (Azure Files / EFS / Filestore).
- `ingress-nginx` (or your cloud ingress) and `cert-manager` with a ClusterIssuer.
- A reachable **managed Postgres** and a Secret holding `DATABASE_URL`:
  ```bash
  kubectl create namespace analytika
  kubectl -n analytika create secret generic analytika-db \
    --from-literal=DATABASE_URL='postgres://user:pass@HOST:5432/analytika'
  ```

## Install

```bash
helm upgrade --install analytika deploy/helm/analytika \
  -n analytika --create-namespace \
  -f my-prod-values.yaml          # based on values-prod.example.yaml
```

Key values: `image.tag`, `database.existingSecret`, `sharedStorage.storageClassName`,
`ingress.hosts[].host`, `web.replicaCount` / `web.autoscaling`.

## Validate before applying

```bash
helm lint deploy/helm/analytika -f deploy/helm/analytika/values-prod.example.yaml
helm template analytika deploy/helm/analytika \
  -f deploy/helm/analytika/values-prod.example.yaml | kubeconform -strict -summary
```

## Verify after deploy

```bash
kubectl -n analytika get job  -l app.kubernetes.io/component=migrate
kubectl -n analytika rollout status deploy/analytika-web
kubectl -n analytika rollout status deploy/analytika-worker
kubectl -n analytika exec deploy/analytika-web -- wget -qO- http://localhost:8080/healthz
```

**Keyring-share check (highest-risk item):** save a portal credential in the UI,
delete a web pod, and confirm the credential still decrypts after it reschedules —
this proves the shared keyring on the RWX volume works.

## Notes
- The single-container deploys (`render.yaml`, `railway.toml`, `docker-compose.yml`)
  are unaffected; `MigrateOnStartup` defaults to `true` so they keep migrating on boot.
- For zero-RWX clusters, run a single web replica and switch `sharedStorage.accessMode`
  to `ReadWriteOnce` (loses horizontal web scaling), or move files to object storage +
  keys to the DB (app change).
