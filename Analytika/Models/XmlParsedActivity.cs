namespace Analytika.Models;

public class XmlParsedActivity
{
    public int Id { get; set; }

    public int XmlParsedRecordId { get; set; }
    public XmlParsedRecord? XmlParsedRecord { get; set; }

    public string? ActivityCode { get; set; }
    public string? ActivityType { get; set; }
    public decimal Quantity { get; set; }
    public decimal Net { get; set; }
    public decimal Gross { get; set; }
    public decimal PaymentAmount { get; set; }
    public string? DenialCode { get; set; }
    public string? Clinician { get; set; }
    public string? Start { get; set; }
    public string? OrderingClinician { get; set; }
}
