using Analytika.Models;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

public static class SqliteSchemaService
{
    public static void EnsureSchema(AppDbContext db, bool createIndexes)
    {
        db.Database.EnsureCreated();

        if (createIndexes)
            CreatePerformanceIndexes(db);

        MigrateColumns(db);
        CreateTables(db);

        if (createIndexes)
            CreateTableIndexes(db);
    }

    private static void CreatePerformanceIndexes(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_PortalFetchLogs_FetchedAt""
                ON ""PortalFetchLogs""(""FetchedAt"" DESC);
            CREATE INDEX IF NOT EXISTS ""IX_PortalFetchLogs_FacilityId_Status""
                ON ""PortalFetchLogs""(""FacilityId"", ""Status"");
            CREATE INDEX IF NOT EXISTS ""IX_PortalFetchLogs_FacilityId_Operation""
                ON ""PortalFetchLogs""(""FacilityId"", ""Operation"");
            CREATE INDEX IF NOT EXISTS ""IX_PortalTransactions_FacilityId_FileDownloaded""
                ON ""PortalTransactions""(""FacilityId"", ""FileDownloaded"");
            CREATE INDEX IF NOT EXISTS ""IX_PortalTransactions_SyncedAt""
                ON ""PortalTransactions""(""SyncedAt"" DESC);
            -- Pending-download lookup: makes DownloadPendingStream's ""what is still
            -- pending"" query an index walk over skinny pending rows instead of a full
            -- table scan through 280k+ fat blob rows (which stalled resume for many
            -- minutes). Built manually on the live DB first (2026-07-27); this line
            -- only guards fresh databases, where the table is small.
            CREATE INDEX IF NOT EXISTS ""IX_PortalTransactions_PendingDl""
                ON ""PortalTransactions""(""Portal"", ""FileDownloaded"", ""FacilityId"", ""Id"");
            -- NOTE: a covering index (all Live-Submission metadata columns) would
            -- make that report index-only/fast, but BUILDING it reads every row's
            -- post-blob columns once — a multi-hour, I/O-heavy scan on the 48 GB
            -- blob table. Do NOT auto-create it here (it would hang app startup).
            -- Build it deliberately in a maintenance window instead:
            --   CREATE INDEX ""IX_PortalTransactions_Submission_Cover"" ON ""PortalTransactions""
            --     (""FileDownloaded"",""FacilityId"",""Id"",""Portal"",""TransactionId"",""Type"",
            --      ""Direction"",""FileId"",""FileName"",""FileDownloadedAt"",""FileSizeBytes"",
            --      ""TransactionDate"",""Payer"",""Amount"",""Operation"",""SyncPeriod"",""SyncedAt"");
        ");
    }

    private static void MigrateColumns(AppDbContext db)
    {
        if (!ColumnExists(db, "ReportRequests", "EmailTo"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ReportRequests"" ADD COLUMN ""EmailTo"" TEXT NULL");

        // Multi-select report filters (JSON arrays) — added for the Advanced Reports repair.
        foreach (var col in new[] { "FacilityIdsJson", "ReceiverIdsJson", "PayerIdsJson",
                                    "ClinicianIdsJson", "DepartmentIdsJson", "EncounterTypesJson" })
            if (!ColumnExists(db, "ReportRequests", col))
                db.Database.ExecuteSqlRaw($@"ALTER TABLE ""ReportRequests"" ADD COLUMN ""{col}"" TEXT NULL");

        if (!ColumnExists(db, "RemittanceClaims", "ClaimCategory"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""RemittanceClaims"" ADD COLUMN ""ClaimCategory"" TEXT NOT NULL DEFAULT 'Unknown'");

        if (!ColumnExists(db, "XmlParsedRecords", "GrossAmount"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""GrossAmount"" REAL NOT NULL DEFAULT 0");

        if (!ColumnExists(db, "XmlParsedRecords", "DiagnosesJson"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""DiagnosesJson"" TEXT NULL");

        if (!ColumnExists(db, "XmlParsedRecords", "PatientGender"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""PatientGender"" TEXT NULL");

        if (!ColumnExists(db, "XmlParsedRecords", "PatientDob"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""PatientDob"" TEXT NULL");

        if (!ColumnExists(db, "XmlParsedRecords", "PatientNationalId"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""PatientNationalId"" TEXT NULL");

        if (!ColumnExists(db, "XmlParsedRecords", "ClaimCategory"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""ClaimCategory"" TEXT NULL");

        // Official DHPO facility license identity, matched from the imported facility list.
        if (!ColumnExists(db, "Facilities", "FullName"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Facilities"" ADD COLUMN ""FullName"" TEXT NULL");
        if (!ColumnExists(db, "Facilities", "LicenseCode"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Facilities"" ADD COLUMN ""LicenseCode"" TEXT NULL");

        // Permanent download-failure marker: set when BOTH the normal and the archive
        // DHPO endpoints return an empty payload for a file. Without it, dead files
        // (43k+ observed) re-churn through every download pass, costing two portal
        // calls each, forever. Flagged rows are excluded from the pending queue.
        if (!ColumnExists(db, "PortalTransactions", "FileUnavailable"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""PortalTransactions"" ADD COLUMN ""FileUnavailable"" INTEGER NOT NULL DEFAULT 0");

        // Zero-yield marker. A downloaded file that parses cleanly to 0 records writes
        // nothing to XmlParsedRecords, so the "not yet parsed" test (NOT EXISTS) selects
        // it again on every future run — 79,518 such files were re-read on each pass,
        // which was ~97% of the work in a 97-minute parse. Marking them once keeps future
        // runs proportional to genuinely new files.
        if (!ColumnExists(db, "PortalTransactions", "ParseYieldedNothing"))
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""PortalTransactions"" ADD COLUMN ""ParseYieldedNothing"" INTEGER NOT NULL DEFAULT 0");
    }

    private static void CreateTables(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""DhpoCodingSets"" (
                ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""Category"" TEXT NOT NULL,
                ""Code"" TEXT NOT NULL,
                ""Name"" TEXT NOT NULL,
                ""SubType"" TEXT NULL,
                ""ExtraJson"" TEXT NULL,
                ""ImportedAt"" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DhpoCodingSets_Category_Code""
                ON ""DhpoCodingSets""(""Category"", ""Code"");
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""SystemSettings"" (
                ""Id""         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""Category""   TEXT NOT NULL,
                ""Key""        TEXT NOT NULL,
                ""Value""      TEXT NULL,
                ""UpdatedAt""  TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SystemSettings_Category_Key""
                ON ""SystemSettings""(""Category"", ""Key"");

            CREATE TABLE IF NOT EXISTS ""ReportSchedules"" (
                ""Id""              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""Name""            TEXT NOT NULL,
                ""ReportType""      TEXT NOT NULL,
                ""CronExpression""  TEXT NOT NULL DEFAULT '0 8 1 * *',
                ""Recipients""      TEXT NOT NULL,
                ""FileFormat""      TEXT NOT NULL DEFAULT 'Excel',
                ""FacilityIdsJson"" TEXT NULL,
                ""ParametersJson""  TEXT NULL,
                ""IsActive""        INTEGER NOT NULL DEFAULT 1,
                ""LastRunAt""       TEXT NULL,
                ""LastRunStatus""   TEXT NULL,
                ""CreatedAt""       TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""RemittanceClaims"" (
                ""Id""                       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""RemittanceTransactionId""  INTEGER NOT NULL REFERENCES ""PortalTransactions""(""Id"") ON DELETE CASCADE,
                ""FacilityId""               INTEGER NOT NULL REFERENCES ""Facilities""(""Id"") ON DELETE CASCADE,
                ""ClaimId""                  TEXT NOT NULL,
                ""PayerClaimId""             TEXT NULL,
                ""PayerCode""                TEXT NULL,
                ""ClinicianLicense""         TEXT NULL,
                ""OriginalAmount""           REAL NOT NULL DEFAULT 0,
                ""PaidAmount""               REAL NOT NULL DEFAULT 0,
                ""DenialCodesJson""          TEXT NULL,
                ""Comments""                 TEXT NULL,
                ""ActivityCount""            INTEGER NOT NULL DEFAULT 0,
                ""SettlementDate""           TEXT NULL,
                ""PaymentReference""         TEXT NULL,
                ""ParsedAt""                 TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS ""XmlParsedRecords"" (
                ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""PortalTransactionId"" INTEGER NOT NULL REFERENCES ""PortalTransactions""(""Id"") ON DELETE CASCADE,
                ""FacilityId""          INTEGER NOT NULL REFERENCES ""Facilities""(""Id"") ON DELETE CASCADE,
                ""RecordKind""          TEXT NOT NULL,
                ""ClaimId""             TEXT NOT NULL,
                ""FileName""            TEXT NULL,
                ""FileId""              TEXT NULL,
                ""TransactionDate""      TEXT NULL,
                ""SenderId""            TEXT NULL,
                ""ReceiverId""          TEXT NULL,
                ""ReceiverName""        TEXT NULL,
                ""PayerId""             TEXT NULL,
                ""PayerName""           TEXT NULL,
                ""PatientId""           TEXT NULL,
                ""MemberId""            TEXT NULL,
                ""TreatmentDate""       TEXT NULL,
                ""TreatmentDateEnd""    TEXT NULL,
                ""DateOfAdmission""     TEXT NULL,
                ""SubmissionDate""      TEXT NULL,
                ""EncounterType""       TEXT NULL,
                ""Clinician""           TEXT NULL,
                ""ServiceYear""         TEXT NULL,
                ""ServiceMonth""        TEXT NULL,
                ""GrossAmount""         REAL NOT NULL DEFAULT 0,
                ""NetAmount""           REAL NOT NULL DEFAULT 0,
                ""PaidAmount""          REAL NOT NULL DEFAULT 0,
                ""ActivityCount""       INTEGER NOT NULL DEFAULT 0,
                ""PaymentReference""    TEXT NULL,
                ""SettlementDate""      TEXT NULL,
                ""DenialCodesJson""     TEXT NULL,
                ""Comments""            TEXT NULL,
                ""IdPayer""             TEXT NULL,
                ""ResubmissionType""    TEXT NULL,
                ""PrincipalDiagnosis""  TEXT NULL,
                ""DiagnosesJson""       TEXT NULL,
                ""PatientGender""       TEXT NULL,
                ""PatientDob""          TEXT NULL,
                ""PatientNationalId""   TEXT NULL,
                ""ClaimCategory""       TEXT NULL,
                ""IsMatched""           INTEGER NOT NULL DEFAULT 0,
                ""ReadyForReport""      INTEGER NOT NULL DEFAULT 1,
                ""Notes""               TEXT NULL,
                ""ParsedAt""            TEXT NOT NULL,
                ""MatchedAt""           TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS ""XmlParsedActivities"" (
                ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""XmlParsedRecordId""   INTEGER NOT NULL REFERENCES ""XmlParsedRecords""(""Id"") ON DELETE CASCADE,
                ""ActivityCode""        TEXT NULL,
                ""ActivityType""        TEXT NULL,
                ""Quantity""            REAL NOT NULL DEFAULT 0,
                ""Net""                 REAL NOT NULL DEFAULT 0,
                ""Gross""               REAL NOT NULL DEFAULT 0,
                ""PaymentAmount""       REAL NOT NULL DEFAULT 0,
                ""DenialCode""          TEXT NULL,
                ""Clinician""           TEXT NULL,
                ""Start""               TEXT NULL,
                ""OrderingClinician""   TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS ""ResubmissionTasks"" (
                ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""RemittanceClaimId""   INTEGER NOT NULL UNIQUE REFERENCES ""RemittanceClaims""(""Id"") ON DELETE CASCADE,
                ""AssignedToUserId""    TEXT NULL REFERENCES ""AspNetUsers""(""Id"") ON DELETE SET NULL,
                ""AssignedByUserId""    TEXT NULL REFERENCES ""AspNetUsers""(""Id"") ON DELETE SET NULL,
                ""AssignedAt""          TEXT NOT NULL DEFAULT (datetime('now')),
                ""DueDate""             TEXT NULL,
                ""Status""              TEXT NOT NULL DEFAULT 'Unassigned',
                ""Priority""            TEXT NOT NULL DEFAULT 'Normal',
                ""Notes""               TEXT NULL,
                ""ActionTaken""         TEXT NULL,
                ""StartedAt""           TEXT NULL,
                ""ResubmittedAt""       TEXT NULL,
                ""ClosedAt""            TEXT NULL,
                ""CreatedAt""           TEXT NOT NULL DEFAULT (datetime('now')),
                ""UpdatedAt""           TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS ""AiUsageLogs"" (
                ""Id""               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""UserId""           TEXT NULL,
                ""UserName""         TEXT NULL,
                ""Question""         TEXT NOT NULL DEFAULT '',
                ""GeneratedSql""     TEXT NULL,
                ""RowsReturned""     INTEGER NOT NULL DEFAULT 0,
                ""PromptTokens""     INTEGER NOT NULL DEFAULT 0,
                ""CompletionTokens"" INTEGER NOT NULL DEFAULT 0,
                ""TotalTokens""      INTEGER NOT NULL DEFAULT 0,
                ""LatencyMs""        INTEGER NOT NULL DEFAULT 0,
                ""Success""          INTEGER NOT NULL DEFAULT 0,
                ""ErrorMessage""     TEXT NULL,
                ""Model""            TEXT NULL,
                ""CreatedAt""        TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS ""IX_AiUsageLogs_CreatedAt"" ON ""AiUsageLogs""(""CreatedAt"" DESC);
            CREATE INDEX IF NOT EXISTS ""IX_AiUsageLogs_UserId"" ON ""AiUsageLogs""(""UserId"");
        ");
    }

    private static void CreateTableIndexes(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_RemittanceClaims_FacilityId"" ON ""RemittanceClaims""(""FacilityId"");
            CREATE INDEX IF NOT EXISTS ""IX_RemittanceClaims_ClaimId""    ON ""RemittanceClaims""(""ClaimId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_PortalTransactionId"" ON ""XmlParsedRecords""(""PortalTransactionId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_Facility_Kind"" ON ""XmlParsedRecords""(""FacilityId"", ""RecordKind"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_ClaimId"" ON ""XmlParsedRecords""(""ClaimId"");
            -- NOCASE variant: MatchParsedRecordsAsync compares ClaimId COLLATE NOCASE,
            -- which cannot use the plain ClaimId index — without this the match UPDATE
            -- degrades to an O(n^2) scan (hours/days at 1M+ rows instead of minutes).
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_Fac_Claim_NC""
                ON ""XmlParsedRecords""(""FacilityId"", ""ClaimId"" COLLATE NOCASE, ""RecordKind"");
            -- Dropdown option lists for the RCM dashboard are DISTINCT queries over these
            -- columns. Without covering indexes each one scans all 1.6M parsed rows, which
            -- dominated the startup pre-warm (minutes, and ~1h under I/O contention).
            -- Built manually on the live DB first (2026-07-29); these guard fresh databases.
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_Receiver""
                ON ""XmlParsedRecords""(""ReceiverName"", ""ReceiverId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_Payer""
                ON ""XmlParsedRecords""(""PayerName"", ""PayerId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_Encounter""
                ON ""XmlParsedRecords""(""EncounterType"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_ReadyForReport"" ON ""XmlParsedRecords""(""ReadyForReport"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedActivities_RecordId"" ON ""XmlParsedActivities""(""XmlParsedRecordId"");
            CREATE INDEX IF NOT EXISTS ""IX_ResubmissionTasks_Status"" ON ""ResubmissionTasks""(""Status"");
            CREATE INDEX IF NOT EXISTS ""IX_ResubmissionTasks_AssignedToUserId"" ON ""ResubmissionTasks""(""AssignedToUserId"");
        ");
    }

    private static bool ColumnExists(AppDbContext db, string table, string column)
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        db.Database.OpenConnection();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
