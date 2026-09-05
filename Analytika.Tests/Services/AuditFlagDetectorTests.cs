using Analytika.Services;
using FluentAssertions;
using Xunit;

namespace Analytika.Tests.Services;

public class AuditFlagDetectorTests
{
    [Fact]
    public void Dsl9Repeat_OnSeventhCalendarDay_IsFlagged()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "9.01"),
            Row("C2", "07/09/2026", "DSL 9.01")
        ]);

        flags.Should().ContainSingle(flag => flag.RuleId == "NC-CONSULT-007" && flag.RelatedClaimId == "C1");
    }

    [Fact]
    public void ConsultationRepeat_OnSeventhDayAfterInitial_IsStillFreeFollowUp()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "9.01"),
            Row("C2", "08/09/2026", "9.01")
        ]);

        flags.Should().ContainSingle(flag => flag.RuleId == "NC-CONSULT-007");
    }

    [Fact]
    public void SameDiagnosis_DaysEightToFourteen_AllowsHalfPriceCodeAtHalfPrice()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "9.01", net: 100),
            Row("C2", "09/09/2026", "9.02", net: 50)
        ]);

        flags.Should().NotContain(flag => flag.RuleId.StartsWith("NC-CONSULT-014"));
    }

    [Fact]
    public void SameDiagnosis_DaysEightToFourteen_FlagsMoreThanHalfPrice()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "10.01", net: 100),
            Row("C2", "12/09/2026", "10.02", net: 60)
        ]);

        flags.Should().ContainSingle(flag => flag.RuleId == "NC-CONSULT-014-PRICE");
    }

    [Fact]
    public void DifferentDiagnosis_DaysEightToFourteen_CannotUseHalfPriceCode()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "9.01", diagnosis: "J01", net: 100),
            Row("C2", "10/09/2026", "9.02", diagnosis: "M54", net: 50)
        ]);

        flags.Should().ContainSingle(flag => flag.RuleId == "NC-CONSULT-014-DX");
    }

    [Fact]
    public void ExactDuplicateService_IsHighSeverity()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "85025", quantity: 1, net: 25),
            Row("C2", "01/09/2026", "85025", quantity: 1, net: 25)
        ]);

        flags.Should().ContainSingle(flag => flag.RuleId == "DHA-DUP-001" && flag.Severity == "High");
    }

    [Fact]
    public void ExactDuplicateService_DoesNotCompareOriginalAgainstResubmission()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/01/2026", "90471", fileName: "ORIGINAL.xml"),
            Row("C1", "01/01/2026", "90471", fileName: "RES-ORIGINAL.xml", resubmissionType: "internal complaint")
        ]);

        flags.Should().NotContain(flag => flag.RuleId == "DHA-DUP-001");
    }

    [Fact]
    public void ExactDuplicateService_StillComparesTwoOriginalSubmissions()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/01/2026", "90471", fileName: "SUB-1.xml"),
            Row("C2", "01/01/2026", "90471", fileName: "SUB-2.xml")
        ]);

        flags.Should().ContainSingle(flag => flag.RuleId == "DHA-DUP-001" && flag.RelatedClaimId == "C1");
    }

    [Fact]
    public void EmergencyDslConsultation_IsFlagged()
    {
        var flags = AuditFlagDetector.Detect([Row("C1", "01/09/2026", "10.01", encounter: "Emergency")]);

        flags.Should().ContainSingle(flag => flag.RuleId == "NC-ED-6108");
    }

    [Fact]
    public void SameDayConsultation_IsFlaggedAcrossDifferentDiagnoses()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "9.01", diagnosis: "J01"),
            Row("C2", "01/09/2026", "10.01", diagnosis: "M54")
        ]);

        flags.Should().ContainSingle(flag => flag.RuleId == "NC-CONSULT-002");
    }

    [Fact]
    public void MissingPatientIdentity_DoesNotCrossMatchClaims()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "9.01", member: "", patient: ""),
            Row("C2", "03/09/2026", "9.01", member: "", patient: "")
        ]);

        flags.Should().NotContain(flag => flag.RuleId == "DHA-DUP-001" || flag.RuleId == "NC-CONSULT-007");
    }

    [Fact]
    public void DifferentMemberOrCondition_DoesNotCreateSevenDayFlag()
    {
        var flags = AuditFlagDetector.Detect([
            Row("C1", "01/09/2026", "9.01", member: "M1", diagnosis: "J01"),
            Row("C2", "03/09/2026", "9.01", member: "M2", diagnosis: "J01"),
            Row("C3", "04/09/2026", "9.01", member: "M1", diagnosis: "M54")
        ]);

        flags.Should().NotContain(flag => flag.RuleId == "NC-CONSULT-007");
    }

    [Fact]
    public void TimedCodeWithoutStart_IsReviewFlag()
    {
        var flags = AuditFlagDetector.Detect([Row("C1", "01/09/2026", "97110", activityStart: "")]);

        flags.Should().ContainSingle(flag => flag.RuleId == "NC-TIME-001" && flag.Severity == "Review");
    }

    private static AuditClaimActivity Row(
        string claimId,
        string date,
        string code,
        string member = "M1",
        string patient = "P1",
        string diagnosis = "J01",
        string encounter = "Outpatient",
        decimal quantity = 1,
        decimal net = 100,
        string activityStart = "09:00",
        string? fileName = null,
        string resubmissionType = "")
        => new(1, "Test Facility", claimId, member, patient, date, encounter, "DR1", diagnosis, "",
            code, "", quantity, net, net, activityStart, "R1", "Receiver", "PAY1", "Payer",
            fileName ?? $"{claimId}.xml", date, resubmissionType);
}
