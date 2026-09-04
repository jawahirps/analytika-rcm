# Analytika two-year backfill runner

This console runner uses the application's registered portal, credential, database, XML parsing,
and matching services. It runs outside the web request process so a long backfill does not consume
the app's request capacity.

The default is a safe dry run. Start by listing the target facilities:

```powershell
dotnet run --project tools/Analytika.Backfill -- --list --db-dir J:\path\to\data
dotnet run --project tools/Analytika.Backfill -- --dry-run --db-dir J:\path\to\data
```

Execution requires two explicit flags:

```powershell
dotnet run --project tools/Analytika.Backfill -- --execute --confirm-write BACKFILL --db-dir J:\path\to\copied-data --workers 12
```

Use `--facility 1,2` for explicit facilities, or `--partition-count 4 --partition-index 0`
to assign a deterministic subset to a separate process/machine. The rolling range defaults to
today minus two years through today and is split into calendar-month requests.

For SQLite, stop the web app and back up/copy its database first. The runner holds a per-directory
lock and serializes all database upserts, XML parsing, and matching. Searches are parallel across
facilities and are bounded by `--workers` (default 12, maximum 32). After successful validation,
replace/deploy the completed database using the normal database migration workflow.
