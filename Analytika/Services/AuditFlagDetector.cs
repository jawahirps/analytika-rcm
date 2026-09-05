using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Analytika.Services;

public sealed record AuditClaimActivity(
    int FacilityId,
    string Facility,
    string ClaimId,
    string MemberId,
    string PatientId,
    string TreatmentDate,
    string EncounterType,
    string Clinician,
    string PrincipalDiagnosis,
    string DiagnosesJson,
    string ActivityCode,
    string ActivityType,
    decimal Quantity,
    decimal Net,
    decimal Gross,
    string ActivityStart,
    string ReceiverId,
    string ReceiverName,
    string PayerId,
    string PayerName,
    string FileName,
    string SubmissionDate,
    string ResubmissionType);

public sealed record AuditFlag(
    string RuleId,
    string FlagType,
    string Severity,
    string Facility,
    string ClaimId,
    string RelatedClaimId,
    string MemberId,
    string ServiceDate,
    string EncounterType,
    string Clinician,
    string Diagnosis,
    string ActivityCode,
    decimal Quantity,
    decimal Net,
    string Reason,
    string Source,
    string RuleVersion,
    string FileName);

public static partial class AuditFlagDetector
{
    public const string NextcareConsultationSource = "https://www.nextcarehealth.com/news-and-cues/medical-bulletin/consultation-rules-for-healthcare-providers/";
    public const string NextcareTimeSource = "https://www.nextcarehealth.com/news-and-cues/medical-bulletin/billing-of-time-based-codes/";
    public const string DhaEthicsSource = "https://www.dha.gov.ae/uploads/012026/Standards%20for%20Code%20of%20Ethics%20and%20Professional%20Conduct%20for%20Health%20Professionals%20V1%20202613351.pdf";
    public const string RuleVersion = "UAE-AUDIT-2026.09.1";

    private static readonly HashSet<string> TimedCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "90832", "90834", "90837", "97110", "97112", "97116", "97530", "97535",
        "96360", "96361", "96365", "96366", "96367", "96368", "96369", "96370", "96371"
    };

    public static IReadOnlyList<AuditFlag> Detect(IEnumerable<AuditClaimActivity> source)
    {
        var rows = source
            .Select(row => new Candidate(row, TryParseDate(row.TreatmentDate), NormalizeCode(row.ActivityCode), DiagnosisKey(row)))
            .Where(row => row.Date.HasValue && !string.IsNullOrWhiteSpace(row.Code))
            .OrderBy(row => row.Date)
            .ThenBy(row => row.Row.ClaimId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var flags = new List<AuditFlag>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Candidate candidate, string ruleId, string type, string severity, string reason, string sourceUrl, string relatedClaim = "")
        {
            var key = $"{ruleId}|{candidate.Row.ClaimId}|{candidate.Code}|{candidate.Date:yyyyMMdd}|{relatedClaim}";
            if (!seen.Add(key)) return;
            flags.Add(ToFlag(candidate, ruleId, type, severity, reason, sourceUrl, relatedClaim));
        }

        var originalSubmissionRows = rows.Where(x => !IsResubmission(x.Row)).ToList();

        foreach (var group in originalSubmissionRows.Where(x => !string.IsNullOrWhiteSpace(IdentityKey(x.Row))).GroupBy(x => new
                 {
                     x.Row.FacilityId,
                     Member = IdentityKey(x.Row),
                     Date = x.Date!.Value.Date,
                     Clinician = Key(x.Row.Clinician),
                     x.Code,
                     x.Row.Quantity,
                     x.Row.Net
                 }))
        {
            var ordered = group.ToList();
            if (ordered.Count < 2) continue;
            var first = ordered[0];
            foreach (var duplicate in ordered.Skip(1))
                Add(duplicate, "DHA-DUP-001", "Exact duplicate service", "High",
                    "Same member, facility, service date, clinician, activity code, quantity and net amount appears more than once.",
                    DhaEthicsSource, first.Row.ClaimId);
        }

        var consultationRows = originalSubmissionRows.Where(x => IsConsultationCode(x.Code) && !string.IsNullOrWhiteSpace(IdentityKey(x.Row))).ToList();
        foreach (var group in consultationRows.GroupBy(x => new
                 {
                     x.Row.FacilityId,
                     Member = IdentityKey(x.Row),
                     Date = x.Date!.Value.Date,
                     Clinician = Key(x.Row.Clinician)
                 }))
        {
            var ordered = group.ToList();
            if (ordered.Count < 2) continue;
            var first = ordered[0];
            foreach (var repeat in ordered.Skip(1))
                Add(repeat, "NC-CONSULT-002", "Same-day repeat consultation", "High",
                    "Multiple consultations for the same member, practitioner and facility were billed on the same day.",
                    NextcareConsultationSource, first.Row.ClaimId);
        }

        foreach (var group in consultationRows.GroupBy(x => new
                 {
                     x.Row.FacilityId,
                     Member = IdentityKey(x.Row),
                     Clinician = Key(x.Row.Clinician)
                 }))
        {
            var ordered = group.OrderBy(x => x.Date).ToList();
            if (ordered.Count < 2) continue;

            var initial = ordered[0];
            for (var i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var days = (int)(current.Date!.Value.Date - initial.Date!.Value.Date).TotalDays;
                if (days > 28)
                {
                    initial = current;
                    continue;
                }

                var sameDiagnosis = current.Diagnosis.Equals(initial.Diagnosis, StringComparison.OrdinalIgnoreCase);
                if (days is >= 1 and <= 7)
                {
                    if (!sameDiagnosis && IsFreeFollowUp(current.Code))
                        Add(current, "NC-CONSULT-007-DX", "Free follow-up diagnosis mismatch", "High",
                            $"Code {current.Code} was used during the seven-day free follow-up window, but the diagnosis differs from the initial consultation.",
                            NextcareConsultationSource, initial.Row.ClaimId);
                    else if (sameDiagnosis && !IsFreeFollowUp(current.Code))
                        Add(current, "NC-CONSULT-007-CODE", "Free follow-up code mismatch", "Medium",
                            $"This same-diagnosis visit occurred {days} day(s) after the initial consultation; the free follow-up window uses code 9.01 or 10.01.",
                            NextcareConsultationSource, initial.Row.ClaimId);
                    else if (sameDiagnosis && IsFreeFollowUp(current.Code) && current.Row.Net > 0.01m)
                        Add(current, "NC-CONSULT-007-PRICE", "Free follow-up was charged", "High",
                            $"Code {current.Code} is a free follow-up within seven days, but a net charge of {current.Row.Net:0.00} was submitted.",
                            NextcareConsultationSource, initial.Row.ClaimId);
                    continue;
                }

                if (days is < 8 or > 28) continue;
                var halfPriceCode = IsHalfPriceFollowUp(current.Code);
                if (!sameDiagnosis && halfPriceCode)
                    Add(current, "NC-CONSULT-028-DX", "Half-price follow-up diagnosis mismatch", "High",
                        $"Code {current.Code} was billed {days} days after the initial consultation, but the diagnosis differs; half-price follow-up is permitted only when the diagnosis remains the same.",
                        NextcareConsultationSource, initial.Row.ClaimId);
                else if (sameDiagnosis && !halfPriceCode)
                    Add(current, "NC-CONSULT-028-CODE", "Paid follow-up code mismatch", "Medium",
                        $"This same-diagnosis consultation occurred {days} days after the initial consultation; weeks 2 through 4 permit codes 9.02, 10.02 or 11.02.",
                        NextcareConsultationSource, initial.Row.ClaimId);
                else if (sameDiagnosis && halfPriceCode && initial.Row.Net > 0m && current.Row.Net > initial.Row.Net / 2m + 0.01m)
                    Add(current, "NC-CONSULT-028-PRICE", "Paid follow-up exceeds half price", "High",
                        $"The follow-up charge ({current.Row.Net:0.00}) exceeds 50% of the initial consultation charge ({initial.Row.Net:0.00}).",
                        NextcareConsultationSource, initial.Row.ClaimId);
            }
        }

        foreach (var row in consultationRows.Where(x => IsEmergency(x.Row.EncounterType) && IsDsl9To11(x.Code)))
            Add(row, "NC-ED-6108", "Emergency consultation code mismatch", "High",
                "Emergency consultation uses DSL 9, 10 or 11; NextCare guidance specifies DSL 61.08.",
                NextcareConsultationSource);

        foreach (var group in rows.Where(x => !string.IsNullOrWhiteSpace(IdentityKey(x.Row)))
                     .GroupBy(x => new { x.Row.FacilityId, Member = IdentityKey(x.Row), Date = x.Date!.Value.Date }))
        {
            var encounterKinds = group.Select(x => EncounterKind(x.Row.EncounterType)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!encounterKinds.Contains("IP") || (!encounterKinds.Contains("OP") && !encounterKinds.Contains("ED"))) continue;
            var inpatient = group.First(x => EncounterKind(x.Row.EncounterType) == "IP");
            foreach (var row in group.Where(x => EncounterKind(x.Row.EncounterType) is "OP" or "ED"))
                Add(row, "NC-IPOP-001", "Inpatient and outpatient same date", "High",
                    "An outpatient or emergency service appears on the same date as an inpatient admission for the same member and facility.",
                    NextcareConsultationSource, inpatient.Row.ClaimId);
        }

        foreach (var row in rows.Where(x => TimedCodes.Contains(x.Code) && string.IsNullOrWhiteSpace(x.Row.ActivityStart)))
            Add(row, "NC-TIME-001", "Time documentation review", "Review",
                "Selected time-based service has no activity start value in parsed claim data; verify start, end and total-time documentation.",
                NextcareTimeSource);

        return flags
            .OrderBy(flag => SeverityRank(flag.Severity))
            .ThenBy(flag => flag.Facility, StringComparer.OrdinalIgnoreCase)
            .ThenBy(flag => flag.ServiceDate, StringComparer.OrdinalIgnoreCase)
            .ThenBy(flag => flag.ClaimId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AuditFlag ToFlag(Candidate item, string ruleId, string type, string severity, string reason, string source, string relatedClaim)
        => new(ruleId, type, severity, item.Row.Facility, item.Row.ClaimId, relatedClaim, item.Row.MemberId,
            item.Date!.Value.ToString("dd/MM/yyyy"), item.Row.EncounterType, item.Row.Clinician,
            item.Diagnosis, item.Code, item.Row.Quantity, item.Row.Net, reason, source, RuleVersion, item.Row.FileName);

    private static int SeverityRank(string severity) => severity switch { "High" => 0, "Medium" => 1, _ => 2 };
    private static string IdentityKey(AuditClaimActivity row) => Key(string.IsNullOrWhiteSpace(row.MemberId) ? row.PatientId : row.MemberId);
    private static bool IsResubmission(AuditClaimActivity row)
        => !string.IsNullOrWhiteSpace(row.ResubmissionType)
            || Path.GetFileName(row.FileName).StartsWith("RES-", StringComparison.OrdinalIgnoreCase);
    private static string Key(string? value) => (value ?? "").Trim().ToUpperInvariant();
    private static bool IsDsl9(string code) => Dsl9Regex().IsMatch(code);
    private static bool IsFreeFollowUp(string code) => code is "9.01" or "10.01";
    private static bool IsHalfPriceFollowUp(string code) => code is "9.02" or "10.02" or "11.02";
    private static bool IsConsultationCode(string code) => DslConsultationRegex().IsMatch(code);
    private static bool IsDsl9To11(string code) => DslConsultationRegex().IsMatch(code);
    private static string NormalizeCode(string? code) => Regex.Replace((code ?? "").Trim().ToUpperInvariant(), "^DSL\\s*", "").Replace(" ", "");
    private static bool IsEmergency(string? value) => EncounterKind(value) == "ED";
    private static string EncounterKind(string? value)
    {
        var normalized = Key(value);
        if (normalized.Contains("EMERGENCY") || normalized is "ER" or "ED") return "ED";
        if (normalized.Contains("INPATIENT") || normalized is "IP") return "IP";
        if (normalized.Contains("OUTPATIENT") || normalized is "OP") return "OP";
        return normalized;
    }

    private static string DiagnosisKey(AuditClaimActivity row)
    {
        if (!string.IsNullOrWhiteSpace(row.PrincipalDiagnosis)) return Key(row.PrincipalDiagnosis);
        try
        {
            using var json = JsonDocument.Parse(row.DiagnosesJson);
            var first = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().FirstOrDefault() : default;
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Code", out var code)) return Key(code.GetString());
        }
        catch { }
        return "DIAGNOSIS-NOT-AVAILABLE";
    }

    public static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact)) return exact.Date;
        if (!value.Contains('/') && !value.Contains('-') && value.Count(char.IsDigit) < 8) return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed.Date : null;
    }

    private sealed record Candidate(AuditClaimActivity Row, DateTime? Date, string Code, string Diagnosis);

    [GeneratedRegex(@"^9(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Dsl9Regex();

    [GeneratedRegex(@"^(?:9|10|11)(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DslConsultationRegex();
}
