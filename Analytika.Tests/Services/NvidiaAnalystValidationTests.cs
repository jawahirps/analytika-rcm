using System.Reflection;
using Analytika.Services;
using FluentAssertions;
using Xunit;

namespace Analytika.Tests.Services;

/// <summary>
/// Exercises the deterministic server-side guard rails of the AI analyst
/// (<c>ValidateAndCap</c> and <c>ExtractSql</c>) via reflection, so the REAL
/// regexes/logic run — not a re-implementation.
/// </summary>
public class NvidiaAnalystValidationTests
{
    private static readonly MethodInfo ValidateAndCapMethod =
        typeof(NvidiaAnalystService).GetMethod("ValidateAndCap",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ExtractSqlMethod =
        typeof(NvidiaAnalystService).GetMethod("ExtractSql",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static (string? sql, string reason) Validate(string sql, int rowLimit = 200, bool allowFullData = false)
    {
        var res = ValidateAndCapMethod.Invoke(null, new object[] { sql, rowLimit, allowFullData })!;
        var t = res.GetType();
        var sqlVal = (string?)t.GetField("Item1")!.GetValue(res);
        var reason = (string)t.GetField("Item2")!.GetValue(res)!;
        return (sqlVal, reason);
    }

    private static string ExtractSql(string raw) =>
        (string)ExtractSqlMethod.Invoke(null, new object[] { raw })!;

    // ---- Valid analytic queries that MUST pass (false-positive detection) ----

    [Theory]
    [InlineData("SELECT COUNT(*) FROM XmlParsedRecords LIMIT 10")]
    [InlineData("WITH t AS (SELECT FacilityId, SUM(NetAmount) n FROM XmlParsedRecords GROUP BY FacilityId) SELECT * FROM t ORDER BY n DESC LIMIT 10")]
    [InlineData("SELECT strftime('%Y-%m', SubmissionDate) ym, SUM(NetAmount) FROM XmlParsedRecords GROUP BY ym LIMIT 24")]
    [InlineData("SELECT substr(TransactionDate,1,7) m, COUNT(*) FROM XmlParsedRecords GROUP BY m LIMIT 24")]
    [InlineData("SELECT PayerName, ROW_NUMBER() OVER (ORDER BY SUM(PaidAmount) DESC) rn FROM XmlParsedRecords GROUP BY PayerName LIMIT 10")]
    [InlineData("SELECT DenialCode, COUNT(*) FROM XmlParsedActivities WHERE DenialCode IS NOT NULL GROUP BY DenialCode LIMIT 50")]
    [InlineData("SELECT json_array_length(DenialCodesJson) d, COUNT(*) FROM XmlParsedRecords GROUP BY d LIMIT 20")]
    [InlineData("SELECT COALESCE(PayerName,'?') p, SUM(NetAmount) FROM XmlParsedRecords GROUP BY p LIMIT 100")]
    [InlineData("SELECT CASE WHEN NetAmount>1000 THEN 'big' ELSE 'small' END b, COUNT(*) FROM XmlParsedRecords GROUP BY b LIMIT 10")]
    [InlineData("SELECT r.ClaimId, a.ActivityCode FROM XmlParsedRecords r JOIN XmlParsedActivities a ON a.XmlParsedRecordId=r.Id LIMIT 20")]
    [InlineData("SELECT RecordKind, COUNT(*) FROM XmlParsedRecords GROUP BY RecordKind UNION ALL SELECT 'ALL', COUNT(*) FROM XmlParsedRecords LIMIT 100")]
    [InlineData("SELECT REPLACE(PayerName,' ','_') p, COUNT(*) FROM XmlParsedRecords GROUP BY p LIMIT 50")]
    public void ValidQueries_ShouldPass(string sql)
    {
        var (result, reason) = Validate(sql);
        result.Should().NotBeNull($"query should be accepted but was rejected: {reason}");
    }

    [Fact]
    public void MissingLimit_ShouldBeInjected()
    {
        var (result, _) = Validate("SELECT COUNT(*) FROM XmlParsedRecords");
        result.Should().NotBeNull();
        result!.ToLowerInvariant().Should().Contain("limit 200");
    }

    // ---- Security: must be rejected (false-negative detection) ----

    [Theory]
    [InlineData("SELECT * FROM AspNetUsers LIMIT 10")]
    [InlineData("SELECT * FROM PortalCredentials LIMIT 10")]
    [InlineData("SELECT Value FROM SystemSettings LIMIT 10")]
    [InlineData("SELECT * FROM sqlite_master LIMIT 10")]
    [InlineData("SELECT * FROM \"sqlite_master\" LIMIT 10")]
    [InlineData("SELECT * FROM AiUsageLogs LIMIT 10")]
    [InlineData("SELECT * FROM __EFMigrationsHistory LIMIT 10")]
    public void SensitiveTables_ShouldBeBlocked(string sql)
    {
        var (result, _) = Validate(sql);
        result.Should().BeNull($"query touching a sensitive table must be blocked: {sql}");
    }

    [Theory]
    [InlineData("UPDATE XmlParsedRecords SET NetAmount=0")]
    [InlineData("DELETE FROM XmlParsedRecords")]
    [InlineData("DROP TABLE XmlParsedRecords")]
    [InlineData("INSERT INTO Facilities(Name) VALUES('x')")]
    [InlineData("SELECT 1; DROP TABLE Facilities")]
    [InlineData("REPLACE INTO Facilities(Id,Name) VALUES(1,'x')")]
    [InlineData("CREATE TABLE t(x)")]
    [InlineData("PRAGMA table_info(XmlParsedRecords)")]
    public void WriteOrDdl_ShouldBeBlocked(string sql)
    {
        var (result, _) = Validate(sql);
        result.Should().BeNull($"write/DDL/multi-statement must be blocked: {sql}");
    }

    [Theory]
    [InlineData("SELECT PatientId FROM XmlParsedRecords LIMIT 10")]
    [InlineData("SELECT PatientNationalId AS x FROM XmlParsedRecords LIMIT 10")]
    [InlineData("SELECT MemberId, COUNT(*) FROM XmlParsedRecords GROUP BY MemberId LIMIT 10")]
    [InlineData("WITH t AS (SELECT PatientDob d FROM XmlParsedRecords) SELECT d FROM t LIMIT 10")]
    public void PhiColumns_Blocked_WhenAllowFullDataFalse(string sql)
    {
        var (result, _) = Validate(sql, allowFullData: false);
        result.Should().BeNull($"PHI column must be blocked when AllowFullData=false: {sql}");
    }

    [Fact]
    public void PhiColumns_Allowed_WhenAllowFullDataTrue()
    {
        var (result, _) = Validate("SELECT PatientId FROM XmlParsedRecords LIMIT 10", allowFullData: true);
        result.Should().NotBeNull();
    }

    // ---- Documented behaviour probes: string literals colliding with keywords ----

    [Theory]
    [InlineData("SELECT COUNT(*) FROM ResubmissionTasks WHERE Status = 'update' LIMIT 10")]
    [InlineData("SELECT COUNT(*) FROM ResubmissionTasks WHERE Status = 'deleted' LIMIT 10")]
    [InlineData("SELECT COUNT(*) FROM XmlParsedRecords WHERE PayerName = 'drop-in clinic' LIMIT 10")]
    public void KeywordLikeStringLiterals_ShouldPass(string sql)
    {
        // Values inside string literals must not be mistaken for write/DDL keywords.
        var (result, _) = Validate(sql);
        result.Should().NotBeNull();
    }

    [Fact]
    public void PragmaTableValuedFunction_ShouldBeBlocked()
    {
        // pragma_table_info(...) is the function form of a PRAGMA and must be blocked too.
        var (result, _) = Validate("SELECT * FROM pragma_table_info('XmlParsedRecords') LIMIT 10");
        result.Should().BeNull();
    }

    // ---- ExtractSql ----

    [Fact]
    public void ExtractSql_StripsFencesAndSemicolon()
    {
        ExtractSql("```sql\nSELECT 1;\n```").Should().Be("SELECT 1");
        ExtractSql("SELECT 1 ;  ").Should().Be("SELECT 1");
    }
}
