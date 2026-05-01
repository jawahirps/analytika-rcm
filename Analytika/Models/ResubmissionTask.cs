namespace Analytika.Models;

public class ResubmissionTask
{
    public int Id { get; set; }

    public int RemittanceClaimId { get; set; }
    public RemittanceClaim? RemittanceClaim { get; set; }

    // Assignment
    public string? AssignedToUserId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }
    public string? AssignedByUserId { get; set; }
    public ApplicationUser? AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DueDate { get; set; }

    // Status lifecycle
    public string Status { get; set; } = ResubmissionStatus.Unassigned;
    public string Priority { get; set; } = ResubmissionPriority.Normal;

    // Coder work notes
    public string? Notes { get; set; }
    public string? ActionTaken { get; set; }   // what the coder did / resubmission ref
    public DateTime? StartedAt { get; set; }
    public DateTime? ResubmittedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class ResubmissionStatus
{
    public const string Unassigned = "Unassigned";
    public const string Assigned = "Assigned";
    public const string InReview = "InReview";
    public const string Resubmitted = "Resubmitted";
    public const string Appealed = "Appealed";
    public const string Closed = "Closed";
    public const string Rejected = "Rejected";

    public static readonly string[] All = [Unassigned, Assigned, InReview, Resubmitted, Appealed, Closed, Rejected];
}

public static class ResubmissionPriority
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";

    public static readonly string[] All = [Urgent, High, Normal, Low];
}
