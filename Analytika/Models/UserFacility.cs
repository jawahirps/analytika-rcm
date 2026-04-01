namespace Analytika.Models;

public class UserFacility
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int FacilityId { get; set; }
    public ApplicationUser? User { get; set; }
    public Facility? Facility { get; set; }
}
