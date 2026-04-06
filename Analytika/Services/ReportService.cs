using System.Globalization;
using System.Xml.Linq;
using Analytika.Models;
using ClosedXML.Excel;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

public class ReportService : IReportService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ReportService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IEmailService _emailService;

    public ReportService(AppDbContext context, ILogger<ReportService> logger, IWebHostEnvironment env, IEmailService emailService)
    {
        _context = context;
        _logger = logger;
        _env = env;
        _emailService = emailService;
    }

    public string GetNextReportId()
    {
        var lastReport = _context.ReportRequests.OrderByDescending(r => r.Id).FirstOrDefault();
        int nextNum = lastReport != null
            ? int.Parse(lastReport.ReportId.Replace("ANA-", "")) + 1
            : 3000001;
        return $"ANA-{nextNum:D7}";
    }

    public async Task<string> QueueReportAsync(ReportRequest request)
    {
        request.ReportId = GetNextReportId();
        request.Status = "Pending";
        request.RequestedAt = DateTime.UtcNow;

        _context.ReportRequests.Add(request);
        await _context.SaveChangesAsync();

        BackgroundJob.Enqueue<IReportService>(s => s.GenerateReportAsync(request.Id));

        return request.ReportId;
    }

    public async Task<(List<ReportRequest> Reports, int Total)> GetReportsAsync(string reportType, int page, int pageSize)
    {
        var query = _context.ReportRequests
            .Include(r => r.Branch)
            .Include(r => r.Receiver)
            .Include(r => r.Payer)
            .Include(r => r.Clinician)
            .Where(r => r.ReportType == reportType)
            .OrderByDescending(r => r.RequestedAt);

        var total = await query.CountAsync();
        var reports = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return (reports, total);
    }

    public async Task<ReportRequest?> GetReportByIdAsync(int id)
    {
        return await _context.ReportRequests
            .Include(r => r.Branch)
            .Include(r => r.Receiver)
            .Include(r => r.Payer)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    // ── Report Generation ──────────────────────────────────────────────

    public async Task GenerateReportAsync(int reportRequestId)
    {
        var report = await _context.ReportRequests
            .Include(r => r.Branch)
            .Include(r => r.Receiver)
            .Include(r => r.Payer)
            .Include(r => r.Clinician)
            .FirstOrDefaultAsync(r => r.Id == reportRequestId);

        if (report == null) return;

        try
        {
            report.Status = "Processing";
            await _context.SaveChangesAsync();

            var reportsDir = Path.Combine(_env.WebRootPath, "reports");
            Directory.CreateDirectory(reportsDir);

            var fileName = $"{report.ReportId}_{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx";
            var filePath = Path.Combine(reportsDir, fileName);

            // ── Load claim transactions ────────────────────────────────
            var claimQuery = _context.PortalTransactions
                .Where(t => t.Portal == "DHA"
                         && t.FileContentXml != null && t.FileContentXml.Length > 10
                         && t.Type == "Claim");

            if (report.BranchId.HasValue)
                claimQuery = claimQuery.Where(t => t.FacilityId == report.BranchId);

            var claimTxs = await claimQuery
                .Select(t => new { t.FileId, t.FileName, t.FacilityId, t.FileContentXml, t.TransactionDate })
                .Take(20000)
                .ToListAsync();

            // ── Load remittance transactions (RA) ──────────────────────
            var raTxs = await _context.PortalTransactions
                .Where(t => t.Portal == "DHA"
                         && t.FileContentXml != null && t.FileContentXml.Length > 10
                         && t.Type != "Claim"
                         && (!report.BranchId.HasValue || t.FacilityId == report.BranchId))
                .Select(t => new { t.FileId, t.FileName, t.TransactionDate, t.FileContentXml })
                .ToListAsync();

            // ── Build RA lookup keyed by ClaimID ──────────────────────
            var raLookup = new Dictionary<string, RaEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var ra in raTxs)
                foreach (var entry in ParseRaXml(ra.FileContentXml!, ra.FileName, ra.TransactionDate))
                    raLookup.TryAdd(entry.ClaimId, entry);

            // ── Facility name lookup ───────────────────────────────────
            var facilityNames = await _context.Facilities
                .ToDictionaryAsync(f => f.Id, f => f.Name);

            // ── Parse claims and build rows ────────────────────────────
            var rows = new List<ClaimRow>();
            foreach (var tx in claimTxs)
            {
                var facilityName = facilityNames.TryGetValue(tx.FacilityId, out var fn) ? fn : $"Facility {tx.FacilityId}";
                foreach (var row in ParseClaimXml(tx.FileContentXml!, tx.FileId, tx.FileName, tx.TransactionDate, facilityName))
                {
                    // Date filter based on SearchCriteria
                    var filterDate = report.SearchCriteria switch
                    {
                        "SubmissionDate"    => ParseDhpoDate(row.SubmissionDate),
                        "EncounterEndDate"  => ParseDhpoDate(row.TreatmentDateEnd),
                        _                  => ParseDhpoDate(row.TreatmentDate)   // default: encounter start
                    };
                    if (filterDate.HasValue &&
                        (filterDate.Value.Date < report.DateFrom.Date || filterDate.Value.Date > report.DateTo.Date))
                        continue;

                    // Payer filter
                    if (report.PayerId.HasValue)
                    {
                        var payerCode = report.Payer?.Name ?? "";
                        if (!string.IsNullOrEmpty(payerCode) && !row.PayerId.Contains(payerCode, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    raLookup.TryGetValue(row.ClaimId, out var ra);
                    row.Ra = ra;
                    rows.Add(row);
                }
            }

            // ── Build Excel ────────────────────────────────────────────
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Claim Summary");

            var headers = new[]
            {
                "Facility", "TransactionRef", "Receiver", "Receiver Name",
                "Payer", "Payer Name", "Patient ID", "Member Id",
                "Treatment Date", "Date Of Admission", "Submission Date",
                "Encounter Type", "Clinician", "Service Year", "Service Month",
                "Submission Level", "Net Amt - Initial Sub", "RA Received Amt",
                "Net Amt - Resubmission", "Approved Amt",
                "Initial Sub Rejected Amt", "Rejected Amt - Resubmission",
                "Unsettled Amt", "Payment Status", "Denial Code",
                "Denial Description", "Payment Ref", "Settlement Date",
                "ID Payer", "Submission File", "RA File", "RA Date", "TAT", "Diagnosis"
            };

            // Header row styling
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1A2F6E");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // Data rows
            for (int i = 0; i < rows.Count; i++)
            {
                var r   = rows[i];
                var ra  = r.Ra;
                var rn  = i + 2;

                var netInitial   = r.NetAmtInitial;
                var approvedAmt  = ra?.ApprovedAmt ?? 0m;
                var receivedAmt  = ra?.ReceivedAmt ?? 0m;
                var unsettled    = ra == null ? netInitial : Math.Max(0m, netInitial - approvedAmt);
                var rejInitial   = ra == null ? 0m : Math.Max(0m, netInitial - approvedAmt);
                var payStatus    = ra == null ? "Pending" : (approvedAmt <= 0 ? "Rejected" : approvedAmt < netInitial - 0.01m ? "Partial" : "Paid");

                // TAT in days
                var tatDays = "";
                if (ra != null && !string.IsNullOrEmpty(ra.SettlementDate))
                {
                    var subDt  = ParseDhpoDate(r.SubmissionDate);
                    var settDt = ParseDhpoDate(ra.SettlementDate);
                    if (subDt.HasValue && settDt.HasValue)
                        tatDays = ((int)(settDt.Value - subDt.Value).TotalDays).ToString();
                }

                ws.Cell(rn, 1).Value  = r.Facility;
                ws.Cell(rn, 2).Value  = r.ClaimId;
                ws.Cell(rn, 3).Value  = r.ReceiverId;
                ws.Cell(rn, 4).Value  = r.ReceiverName;
                ws.Cell(rn, 5).Value  = r.PayerId;
                ws.Cell(rn, 6).Value  = r.PayerName;
                ws.Cell(rn, 7).Value  = r.PatientId;
                ws.Cell(rn, 8).Value  = r.MemberId;
                ws.Cell(rn, 9).Value  = r.TreatmentDate;
                ws.Cell(rn, 10).Value = r.DateOfAdmission;
                ws.Cell(rn, 11).Value = r.SubmissionDate;
                ws.Cell(rn, 12).Value = r.EncounterType;
                ws.Cell(rn, 13).Value = r.Clinician;
                ws.Cell(rn, 14).Value = r.ServiceYear;
                ws.Cell(rn, 15).Value = r.ServiceMonth;
                ws.Cell(rn, 16).Value = r.SubmissionLevel;
                ws.Cell(rn, 17).Value = netInitial;
                ws.Cell(rn, 18).Value = receivedAmt;
                ws.Cell(rn, 19).Value = 0m;           // Resubmission net — not yet available
                ws.Cell(rn, 20).Value = approvedAmt;
                ws.Cell(rn, 21).Value = rejInitial;
                ws.Cell(rn, 22).Value = 0m;           // Rejected resubmission
                ws.Cell(rn, 23).Value = unsettled;
                ws.Cell(rn, 24).Value = payStatus;
                ws.Cell(rn, 25).Value = ra?.DenialCode ?? "";
                ws.Cell(rn, 26).Value = ra?.DenialDescription ?? "";
                ws.Cell(rn, 27).Value = ra?.PaymentRef ?? "";
                ws.Cell(rn, 28).Value = ra?.SettlementDate ?? "";
                ws.Cell(rn, 29).Value = r.IdPayer;
                ws.Cell(rn, 30).Value = r.SubmissionFile;
                ws.Cell(rn, 31).Value = ra?.RaFile ?? "";
                ws.Cell(rn, 32).Value = ra?.RaDate ?? "";
                ws.Cell(rn, 33).Value = tatDays;
                ws.Cell(rn, 34).Value = r.PrincipalDiagnosis;

                // Zebra stripe
                if (i % 2 == 1)
                    ws.Row(rn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8F9FA");

                // Amount columns format
                foreach (var col in new[] { 17, 18, 19, 20, 21, 22, 23 })
                    ws.Cell(rn, col).Style.NumberFormat.Format = "#,##0.00";
            }

            ws.Row(1).Height = 20;
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            // Auto-filter
            ws.RangeUsed()?.SetAutoFilter();

            wb.SaveAs(filePath);

            report.Status = "Completed";
            report.GeneratedAt = DateTime.UtcNow;
            report.FilePath = $"/reports/{fileName}";

            // Send email if recipients were specified
            if (!string.IsNullOrWhiteSpace(report.EmailTo))
                await _emailService.SendReportAsync(report.EmailTo, report.ReportId, report.ReportType, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate report {ReportId}", report.ReportId);
            report.Status = "Failed";
        }

        await _context.SaveChangesAsync();
    }

    // ── XML parsers ────────────────────────────────────────────────────

    private static IEnumerable<ClaimRow> ParseClaimXml(
        string xml, string? fileId, string? fileName, string? txDate, string facilityName)
    {
        if (string.IsNullOrEmpty(xml)) yield break;
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { yield break; }

        var header      = doc.Root?.Element("Header");
        var receiverId  = header?.Element("ReceiverID")?.Value ?? "";
        var submDate    = header?.Element("TransactionDate")?.Value ?? txDate ?? "";

        foreach (var claim in doc.Descendants("Claim"))
        {
            var enc           = claim.Element("Encounter");
            var treatStart    = enc?.Element("Start")?.Value ?? "";
            var treatEnd      = enc?.Element("End")?.Value ?? "";
            var encTypeRaw    = enc?.Element("Type")?.Value ?? "";
            var encType       = MapEncounterType(encTypeRaw);
            var clinician     = claim.Descendants("Activity")
                                     .FirstOrDefault()?.Element("Clinician")?.Value ?? "";
            var principalDiag = claim.Elements("Diagnosis")
                                     .FirstOrDefault(d => d.Element("Type")?.Value == "Principal")
                                     ?.Element("Code")?.Value ?? "";

            var serviceYear  = "";
            var serviceMonth = "";
            var admDate      = "";
            if (DateTime.TryParseExact(treatStart, "dd/MM/yyyy HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var td))
            {
                serviceYear  = td.Year.ToString();
                serviceMonth = td.ToString("MMMM");
                if (encTypeRaw == "2") admDate = treatStart; // inpatient only
            }

            decimal.TryParse(claim.Element("Net")?.Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out var net);

            yield return new ClaimRow
            {
                Facility         = facilityName,
                ClaimId          = claim.Element("ID")?.Value ?? "",
                ReceiverId       = receiverId,
                ReceiverName     = receiverId,  // plain code — no receiver name table
                PayerId          = claim.Element("PayerID")?.Value ?? "",
                PayerName        = claim.Element("PayerID")?.Value ?? "",
                PatientId        = enc?.Element("PatientID")?.Value ?? "",
                MemberId         = claim.Element("MemberID")?.Value ?? "",
                TreatmentDate    = treatStart,
                TreatmentDateEnd = treatEnd,
                DateOfAdmission  = admDate,
                SubmissionDate   = submDate,
                EncounterType    = encType,
                Clinician        = clinician,
                ServiceYear      = serviceYear,
                ServiceMonth     = serviceMonth,
                SubmissionLevel  = "Initial",
                NetAmtInitial    = net,
                IdPayer          = claim.Element("IDPayer")?.Value ?? "",
                SubmissionFile   = fileName ?? fileId ?? "",
                PrincipalDiagnosis = principalDiag
            };
        }
    }

    private static IEnumerable<RaEntry> ParseRaXml(string xml, string? fileName, string? txDate)
    {
        if (string.IsNullOrEmpty(xml)) yield break;
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { yield break; }

        var header  = doc.Root?.Element("Header");
        var raDate  = header?.Element("TransactionDate")?.Value ?? txDate ?? "";
        var payRef  = header?.Element("PaymentReference")?.Value
                   ?? doc.Root?.Element("PaymentReference")?.Value ?? "";

        foreach (var claim in doc.Descendants("Claim"))
        {
            var claimId = claim.Element("ID")?.Value ?? claim.Element("ClaimID")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(claimId)) continue;

            decimal.TryParse(claim.Element("Net")?.Value ?? claim.Element("PaidAmount")?.Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out var approved);
            decimal.TryParse(claim.Element("Gross")?.Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out var received);

            var denialCode = claim.Descendants("Denial").FirstOrDefault()?.Element("Code")?.Value
                          ?? claim.Element("DenialCode")?.Value ?? "";
            var denialDesc = claim.Descendants("Denial").FirstOrDefault()?.Element("Description")?.Value
                          ?? claim.Element("DenialDescription")?.Value ?? "";

            yield return new RaEntry
            {
                ClaimId          = claimId,
                ApprovedAmt      = approved,
                ReceivedAmt      = received,
                RaFile           = fileName ?? "",
                RaDate           = raDate,
                SettlementDate   = raDate,
                PaymentRef       = payRef,
                DenialCode       = denialCode,
                DenialDescription = denialDesc,
                Status           = approved <= 0 ? "Rejected" : "Paid"
            };
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static DateTime? ParseDhpoDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s, "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }

    private static string MapEncounterType(string? code) => code switch
    {
        "1" => "Outpatient",
        "2" => "Inpatient",
        "3" => "Emergency",
        "4" => "Dental",
        _   => code ?? ""
    };

    // ── Internal DTOs ──────────────────────────────────────────────────

    private class ClaimRow
    {
        public string Facility { get; set; } = "";
        public string ClaimId { get; set; } = "";
        public string ReceiverId { get; set; } = "";
        public string ReceiverName { get; set; } = "";
        public string PayerId { get; set; } = "";
        public string PayerName { get; set; } = "";
        public string PatientId { get; set; } = "";
        public string MemberId { get; set; } = "";
        public string TreatmentDate { get; set; } = "";
        public string TreatmentDateEnd { get; set; } = "";
        public string DateOfAdmission { get; set; } = "";
        public string SubmissionDate { get; set; } = "";
        public string EncounterType { get; set; } = "";
        public string Clinician { get; set; } = "";
        public string ServiceYear { get; set; } = "";
        public string ServiceMonth { get; set; } = "";
        public string SubmissionLevel { get; set; } = "Initial";
        public decimal NetAmtInitial { get; set; }
        public string IdPayer { get; set; } = "";
        public string SubmissionFile { get; set; } = "";
        public string PrincipalDiagnosis { get; set; } = "";
        public RaEntry? Ra { get; set; }
    }

    private class RaEntry
    {
        public string ClaimId { get; set; } = "";
        public decimal? ApprovedAmt { get; set; }
        public decimal? ReceivedAmt { get; set; }
        public string RaFile { get; set; } = "";
        public string RaDate { get; set; } = "";
        public string SettlementDate { get; set; } = "";
        public string PaymentRef { get; set; } = "";
        public string DenialCode { get; set; } = "";
        public string DenialDescription { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
