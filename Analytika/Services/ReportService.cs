using System.Globalization;
using System.Xml.Linq;
using Analytika.Models;
using ClosedXML.Excel;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Analytika.Services;

public class ReportService : IReportService
{
    private const string GhafInk = "#003B4D";
    private const string GhafPrimary = "#003B4D";
    private const string GhafTeal = "#008B8B";
    private const string GhafPale = "#C6E2E9";
    private const string GhafCream = "#F7F9F9";
    private const string GhafBorder = "#C6E2E9";

    private readonly AppDbContext _context;
    private readonly ILogger<ReportService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IEmailService _emailService;
    private readonly RemittanceParserService _remittanceParser;
    private readonly XmlParsingService _xmlParsingService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public ReportService(
        AppDbContext context,
        ILogger<ReportService> logger,
        IWebHostEnvironment env,
        IEmailService emailService,
        RemittanceParserService remittanceParser,
        XmlParsingService xmlParsingService,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _env = env;
        _emailService = emailService;
        _remittanceParser = remittanceParser;
        _xmlParsingService = xmlParsingService;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
    }

    public string GetNextReportId(ReportRequest request, string? selectedDateRange = null)
    {
        var facilityName = "All";
        if (request.BranchId.HasValue)
        {
            facilityName = _context.Facilities
                .Where(f => f.Id == request.BranchId.Value)
                .Select(f => f.Name)
                .FirstOrDefault() ?? "Facility";
        }

        var dateRange = NormalizeReportIdSegment(selectedDateRange)
            ?? NormalizeReportIdSegment(BuildDateRangeLabel(request.DateFrom, request.DateTo))
            ?? "Range";

        var generatedDate = DateTime.Now.ToString("yyyyMMddHHmmss");
        return $"{NormalizeReportIdSegment(facilityName)}-{dateRange}-{generatedDate}";
    }

    public async Task<string> QueueReportAsync(ReportRequest request, string? selectedDateRange = null)
    {
        request.ReportId = GetNextReportId(request, selectedDateRange);
        request.Status = "Pending";
        request.RequestedAt = DateTime.UtcNow;

        _context.ReportRequests.Add(request);
        await _context.SaveChangesAsync();

        var facilityName = request.BranchId.HasValue
            ? (await _context.Facilities
                .Where(f => f.Id == request.BranchId.Value)
                .Select(f => f.Name)
                .FirstOrDefaultAsync()) ?? "Facility"
            : "All";

        ReportGenerationState.Start(
            request.Id,
            request.ReportId,
            request.ReportType,
            facilityName,
            selectedDateRange ?? BuildDateRangeLabel(request.DateFrom, request.DateTo));

        if (_configuration.GetValue("BackgroundJobs:HangfireServerEnabled", false))
        {
            BackgroundJob.Enqueue<IReportService>(s => s.GenerateReportAsync(request.Id));
        }
        else
        {
            _logger.LogInformation("Hangfire server is disabled; generating report {ReportId} in background.", request.ReportId);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IReportService>();
                    await service.GenerateReportAsync(request.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background report runner failed for {ReportId}", request.ReportId);
                    ReportGenerationState.Fail($"Report {request.ReportId} could not start.");
                }
            });
        }

        return request.ReportId;
    }

    private static string? NormalizeReportIdSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var segment = new string(value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        while (segment.Contains("--", StringComparison.Ordinal))
            segment = segment.Replace("--", "-", StringComparison.Ordinal);

        return segment.Trim('-');
    }

    private static string BuildDateRangeLabel(DateTime from, DateTime to)
    {
        if (from.Date == to.Date)
            return from.ToString("yyyyMMdd");

        return $"{from:yyyyMMdd}-{to:yyyyMMdd}";
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

            void UpdateStage(string stage, int pct, int done = 0, int total = 0, string? message = null)
                => ReportGenerationState.Update(stage, pct, done, total, message);

            UpdateStage("Preparing query plan", 3, 0, 0, $"ReportRequests #{report.Id}: facility={report.Branch?.Name ?? "All"}, range={report.DateFrom:dd/MM/yyyy}-{report.DateTo:dd/MM/yyyy}.");
            UpdateStage("Preparing parsed XML", 5, 0, 0, "Checking claim-level XML cache before report matching.");
            XmlParsingRunResult parseResult;
            if (report.BranchId.HasValue)
            {
                parseResult = await _xmlParsingService.ParseDownloadedXmlAsync(report.BranchId, rebuild: false, onProgress: p =>
                {
                    var pct = p.Total > 0 ? 5 + (int)Math.Round((p.Done / (double)p.Total) * 15) : 15;
                    UpdateStage("Preparing parsed XML", Math.Min(20, pct), p.Done, p.Total, p.Message);
                    return Task.CompletedTask;
                });
            }
            else
            {
                await _xmlParsingService.EnsureSchemaAsync();
                var matchResult = await _xmlParsingService.MatchParsedRecordsAsync();
                parseResult = new XmlParsingRunResult { MatchedClaimRefs = matchResult.MatchedClaimRefs };
                UpdateStage("Preparing parsed XML", 20, 0, 0, "Using prepared all-facility XML cache. Prepare or rebuild from Portal > XML Parsing when new files are downloaded.");
            }
            UpdateStage("Preparing parsed XML", 20, parseResult.RecordsSaved, parseResult.FilesScanned,
                $"XML cache ready: {parseResult.RecordsSaved:N0} new claim row(s), {parseResult.MatchedClaimRefs:N0} matched claim ref(s).");

            UpdateStage("Loading payer lookup", 18, 0, 0, "Query: DhpoCodingSets where Category = Payer.");
            var payerLookup = await LoadPayerLookupAsync();

            // ── Load parsed outbound claim submissions ─────────────────
            UpdateStage("Querying parsed submissions", 25, 0, 0, "Query: XmlParsedRecords where RecordKind = Submission and ReadyForReport = true.");
            var parsedClaimQuery = _context.XmlParsedRecords
                .AsNoTracking()
                .Where(r => r.ReadyForReport && r.RecordKind == "Submission");

            if (report.BranchId.HasValue)
                parsedClaimQuery = parsedClaimQuery.Where(r => r.FacilityId == report.BranchId.Value);

            var parsedSubmissions = await parsedClaimQuery
                .OrderBy(r => r.ParsedAt)
                .ToListAsync();
            UpdateStage("Loading parsed submissions", 35, parsedSubmissions.Count, parsedSubmissions.Count, $"Loaded {parsedSubmissions.Count:N0} parsed submission claim row(s).");

            // ── Load parsed remittance rows and build a claim-id lookup ──
            UpdateStage("Querying parsed remittances", 42, 0, 0, "Query: XmlParsedRecords where RecordKind = Remittance and ReadyForReport = true.");
            var parsedRemittanceQuery = _context.XmlParsedRecords
                .AsNoTracking()
                .Where(r => r.ReadyForReport && r.RecordKind == "Remittance");

            if (report.BranchId.HasValue)
                parsedRemittanceQuery = parsedRemittanceQuery.Where(r => r.FacilityId == report.BranchId.Value);

            var remittanceClaims = await parsedRemittanceQuery
                .Select(r => new RemittanceClaimRow
                {
                    ClaimId = r.ClaimId,
                    PaidAmount = r.PaidAmount,
                    OriginalAmount = r.NetAmount,
                    SettlementDate = r.SettlementDate,
                    PaymentReference = r.PaymentReference,
                    DenialCodesJson = r.DenialCodesJson,
                    Comments = r.Comments,
                    FileName = r.FileName,
                    TransactionDate = r.TransactionDate
                })
                .ToListAsync();
            UpdateStage("Loading parsed remittances", 48, remittanceClaims.Count, remittanceClaims.Count, $"Loaded {remittanceClaims.Count:N0} parsed remittance claim row(s).");

            var raLookup = AggregateRemittances(remittanceClaims);
            UpdateStage("Matching inbound and outbound", 55, raLookup.Count, remittanceClaims.Count, $"Matched {raLookup.Count:N0} remittance claim(s) by Claim ID.");

            // ── Facility name lookup ───────────────────────────────────
            UpdateStage("Loading facility lookup", 58, 0, 0, "Query: Facilities lookup for report row labels.");
            var facilityNames = await _context.Facilities.AsNoTracking()
                .ToDictionaryAsync(f => f.Id, f => f.Name);

            // ── Build rows only after both sides are parsed and matched ──
            var rows = new List<ClaimRow>();
            var outboundCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var outboundResubTypes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var inboundCounts = remittanceClaims
                .Where(rc => !string.IsNullOrWhiteSpace(rc.ClaimId))
                .GroupBy(rc => rc.ClaimId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            void AddToLookup(Dictionary<string, HashSet<string>> lookup, string claimId, string value)
            {
                if (string.IsNullOrWhiteSpace(claimId) || string.IsNullOrWhiteSpace(value))
                    return;

                if (!lookup.TryGetValue(claimId, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    lookup[claimId] = set;
                }
                set.Add(value.Trim());
            }

            var allSubmissionRows = parsedSubmissions
                .Select(parsed =>
                {
                    var facilityName = facilityNames.TryGetValue(parsed.FacilityId, out var fn) ? fn : $"Facility {parsed.FacilityId}";
                    return MapParsedSubmission(parsed, facilityName, payerLookup);
                })
                .Where(r => !string.IsNullOrWhiteSpace(r.ClaimId))
                .ToList();

            foreach (var row in allSubmissionRows)
            {
                outboundCounts[row.ClaimId] = outboundCounts.TryGetValue(row.ClaimId, out var count) ? count + 1 : 1;
                AddToLookup(outboundResubTypes, row.ClaimId, row.ResubmissionType);
            }

            var resubmissionRows = allSubmissionRows.Where(IsResubmissionRow).ToList();
            var initialSubmissionRows = allSubmissionRows.Where(r => !IsResubmissionRow(r)).ToList();
            var resubmissionByClaim = AggregateResubmissions(resubmissionRows);
            UpdateStage("Separating submissions", 58, initialSubmissionRows.Count, allSubmissionRows.Count,
                $"Using {initialSubmissionRows.Count:N0} initial submission row(s) as claim line items; {resubmissionRows.Count:N0} resubmission row(s) kept for calculations.");

            for (int i = 0; i < initialSubmissionRows.Count; i++)
            {
                var row = initialSubmissionRows[i];

                // Date filter based on SearchCriteria
                var filterDate = report.SearchCriteria switch
                {
                    "SubmissionDate" => ParseDhpoDate(row.SubmissionDate),
                    "EncounterEndDate" => ParseDhpoDate(row.TreatmentDateEnd),
                    _ => ParseDhpoDate(row.TreatmentDate)
                };
                if (filterDate.HasValue &&
                    (filterDate.Value.Date < report.DateFrom.Date || filterDate.Value.Date > report.DateTo.Date))
                    continue;

                if (report.PayerId.HasValue)
                {
                    var payerCode = report.Payer?.Name ?? "";
                    if (!string.IsNullOrEmpty(payerCode)
                        && !row.PayerName.Contains(payerCode, StringComparison.OrdinalIgnoreCase)
                        && !row.PayerId.Contains(payerCode, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                raLookup.TryGetValue(row.ClaimId, out var ra);
                row.Ra = ra;

                var outboundCount = !string.IsNullOrWhiteSpace(row.ClaimId) && outboundCounts.TryGetValue(row.ClaimId, out var obCount) ? obCount : 1;
                var inboundCount = !string.IsNullOrWhiteSpace(row.ClaimId) && inboundCounts.TryGetValue(row.ClaimId, out var ibCount) ? ibCount : 0;
                var resubTypes = !string.IsNullOrWhiteSpace(row.ClaimId) && outboundResubTypes.TryGetValue(row.ClaimId, out var types) ? types : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                resubmissionByClaim.TryGetValue(row.ClaimId, out var resubmission);

                row.OutboundCount = outboundCount;
                row.InboundCount = inboundCount;
                row.RecordCount = outboundCount + inboundCount;
                row.SubmissionLevel = DetermineSubmissionLevel(outboundCount, inboundCount, resubTypes);
                row.NetAmtResubmission = resubmission?.NetAmount ?? 0m;
                row.ResubmissionFile = resubmission?.Files ?? "";

                if (!string.IsNullOrWhiteSpace(row.ResubmissionFile))
                    row.SubmissionFile = $"{row.SubmissionFile} | Resub: {row.ResubmissionFile}";
                if (ra != null)
                {
                    row.RaFile = ra.RaFile;
                    row.RaDate = ra.RaDate;
                }
                rows.Add(row);

                if (i == 0 || (i + 1) % 500 == 0 || i + 1 == initialSubmissionRows.Count)
                {
                    var pct = 50 + (int)Math.Round(((i + 1) / (double)Math.Max(1, initialSubmissionRows.Count)) * 35);
                    UpdateStage("Matching inbound and outbound", Math.Min(85, pct), i + 1, initialSubmissionRows.Count, $"Matched {i + 1:N0} of {initialSubmissionRows.Count:N0} initial claim row(s).");
                }
            }

            var exportRows = rows
                .GroupBy(r => r.ClaimId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(r =>
                {
                    var dt = ParseDhpoDate(r.SubmissionDate) ?? ParseDhpoDate(r.TreatmentDate);
                    return dt ?? DateTime.MaxValue;
                }).First())
                .ToList();

            var matchedSubmissionIds = exportRows
                .Where(r => !string.IsNullOrWhiteSpace(r.ClaimId))
                .Select(r => r.ClaimId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unmatchedRemittances = remittanceClaims
                .Where(r => !string.IsNullOrWhiteSpace(r.ClaimId))
                .Where(r => !matchedSubmissionIds.Contains(r.ClaimId))
                .Where(r => IsRemittanceWithinReportRange(r, report.DateFrom, report.DateTo))
                .Select(r => new UnmatchedRemittanceRow
                {
                    TransactionRef = r.ClaimId.Trim(),
                    RemittanceFileName = string.IsNullOrWhiteSpace(r.FileName) ? "-" : r.FileName.Trim()
                })
                .GroupBy(r => $"{r.TransactionRef}\u001F{r.RemittanceFileName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(r => r.TransactionRef, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.RemittanceFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ── Build Excel ────────────────────────────────────────────
            UpdateStage("Generating workbook", 90, exportRows.Count, exportRows.Count, $"Grouping complete; writing {exportRows.Count:N0} matched row(s) to Excel and {unmatchedRemittances.Count:N0} unmatched remittance note(s).");
            using var wb = new XLWorkbook();
            wb.Style.Font.FontName = "Inter";
            var ws = wb.Worksheets.Add("Claim Summary");
            const int tableHeaderRow = 8;

            var headers = new[]
            {
                "Facility", "TransactionRef", "Receiver", "Receiver Name",
                "Payer", "Payer Name", "Patient ID", "Member Id",
                "Treatment Date", "Date Of Admission", "Submission Date",
                "Encounter Type", "Clinician", "Service Year", "Service Month",
                "Record Count", "Submission Level", "Net Amt - Initial Sub", "RA Received Amt",
                "Net Amt - Resubmission", "Approved Amt",
                "Initial Sub Rejected Amt", "Rejected Amt - Resubmission",
                "Unsettled Amt", "Payment Status", "Denial Code",
                "Denial Description", "Payment Ref", "Settlement Date",
                "ID Payer", "Submission File", "RA File", "RA Date", "TAT", "Diagnosis"
            };

            ApplyGhafReportHeader(ws, headers.Length, report, exportRows.Count, unmatchedRemittances.Count);

            // Header row styling
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(tableHeaderRow, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml(GhafPrimary);
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
            var tableHeaderRange = ws.Range(tableHeaderRow, 1, tableHeaderRow, headers.Length);
            tableHeaderRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            tableHeaderRange.Style.Border.BottomBorderColor = XLColor.FromHtml(GhafTeal);

            // Data rows
            for (int i = 0; i < exportRows.Count; i++)
            {
                var r = exportRows[i];
                var ra = r.Ra;
                var rn = tableHeaderRow + 1 + i;

                var netInitial = r.NetAmtInitial;
                var netResubmission = r.NetAmtResubmission;
                var approvedAmt = ra?.ApprovedAmt ?? 0m;
                var receivedAmt = ra?.ReceivedAmt ?? 0m;
                var balanceAmount = netResubmission > 0m ? netResubmission : netInitial;
                var unsettled = ra == null ? balanceAmount : Math.Max(0m, balanceAmount - approvedAmt);
                var rejInitial = ra == null ? 0m : Math.Max(0m, netInitial - approvedAmt);
                var rejResubmission = netResubmission > 0m && ra != null ? Math.Max(0m, netResubmission - approvedAmt) : 0m;
                var payStatus = ra == null ? "Pending" : (approvedAmt <= 0 ? "Rejected" : approvedAmt < balanceAmount - 0.01m ? "Partial" : "Paid");

                // TAT in days
                var tatDays = "";
                if (ra != null && ra.SettlementDateValue.HasValue)
                {
                    var subDt = ParseDhpoDate(r.SubmissionDate);
                    if (subDt.HasValue)
                        tatDays = ((int)(ra.SettlementDateValue.Value.Date - subDt.Value.Date).TotalDays).ToString();
                }

                ws.Cell(rn, 1).Value = r.Facility;
                ws.Cell(rn, 2).Value = r.ClaimId;
                ws.Cell(rn, 3).Value = r.ReceiverId;
                ws.Cell(rn, 4).Value = r.ReceiverName;
                ws.Cell(rn, 5).Value = r.PayerId;
                ws.Cell(rn, 6).Value = r.PayerName;
                ws.Cell(rn, 7).Value = r.PatientId;
                ws.Cell(rn, 8).Value = r.MemberId;
                ws.Cell(rn, 9).Value = r.TreatmentDate;
                ws.Cell(rn, 10).Value = r.DateOfAdmission;
                ws.Cell(rn, 11).Value = r.SubmissionDate;
                ws.Cell(rn, 12).Value = r.EncounterType;
                ws.Cell(rn, 13).Value = r.Clinician;
                ws.Cell(rn, 14).Value = r.ServiceYear;
                ws.Cell(rn, 15).Value = r.ServiceMonth;
                ws.Cell(rn, 16).Value = r.RecordCount;
                ws.Cell(rn, 17).Value = r.SubmissionLevel;
                ws.Cell(rn, 18).Value = netInitial;
                ws.Cell(rn, 19).Value = receivedAmt;
                ws.Cell(rn, 20).Value = netResubmission;
                ws.Cell(rn, 21).Value = approvedAmt;
                ws.Cell(rn, 22).Value = rejInitial;
                ws.Cell(rn, 23).Value = rejResubmission;
                ws.Cell(rn, 24).Value = unsettled;
                ws.Cell(rn, 25).Value = payStatus;
                ws.Cell(rn, 26).Value = ra?.DenialCode ?? "";
                ws.Cell(rn, 27).Value = ra?.DenialDescription ?? "";
                ws.Cell(rn, 28).Value = ra?.PaymentRef ?? "";
                ws.Cell(rn, 29).Value = ra?.SettlementDate ?? "";
                ws.Cell(rn, 30).Value = r.IdPayer;
                ws.Cell(rn, 31).Value = r.SubmissionFile;
                ws.Cell(rn, 32).Value = r.RaFile;
                ws.Cell(rn, 33).Value = r.RaDate;
                ws.Cell(rn, 34).Value = tatDays;
                ws.Cell(rn, 35).Value = r.PrincipalDiagnosis;

                // Zebra stripe
                if (i % 2 == 1)
                    ws.Row(rn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7FCFA");

                // Amount columns format
                foreach (var col in new[] { 18, 19, 20, 21, 22, 23, 24 })
                    ws.Cell(rn, col).Style.NumberFormat.Format = "#,##0.00";

                ws.Row(rn).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                ws.Row(rn).Style.Border.BottomBorderColor = XLColor.FromHtml("#D9EFEA");
            }

            if (unmatchedRemittances.Count > 0)
            {
                var noteRow = tableHeaderRow + exportRows.Count + 3;
                ws.Cell(noteRow, 1).Value = "Ledger Note";
                ws.Cell(noteRow, 1).Style.Font.Bold = true;
                ws.Cell(noteRow, 1).Style.Font.FontColor = XLColor.White;
                ws.Range(noteRow, 1, noteRow, 3).Merge().Style.Fill.BackgroundColor = XLColor.FromHtml("#991B1B");

                ws.Cell(noteRow + 1, 1).Value = "Unmatched Remittance records found";
                ws.Range(noteRow + 1, 1, noteRow + 1, 3).Merge();
                ws.Cell(noteRow + 1, 1).Style.Font.Bold = true;
                ws.Cell(noteRow + 1, 1).Style.Font.FontColor = XLColor.FromHtml("#991B1B");
                ws.Cell(noteRow + 1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEE2E2");

                ws.Cell(noteRow + 2, 1).Value = "Transaction Ref";
                ws.Cell(noteRow + 2, 2).Value = "Remittance file name";
                ws.Range(noteRow + 2, 1, noteRow + 2, 2).Style.Font.Bold = true;
                ws.Range(noteRow + 2, 1, noteRow + 2, 2).Style.Fill.BackgroundColor = XLColor.FromHtml(GhafPale);

                for (int i = 0; i < unmatchedRemittances.Count; i++)
                {
                    var note = unmatchedRemittances[i];
                    var rn = noteRow + 3 + i;
                    ws.Cell(rn, 1).Value = note.TransactionRef;
                    ws.Cell(rn, 2).Value = note.RemittanceFileName;
                }
            }

            // Auto-filter
            var mainTableLastRow = tableHeaderRow + Math.Max(0, exportRows.Count);
            ws.Range(tableHeaderRow, 1, mainTableLastRow, headers.Length).SetAutoFilter();

            ws.Row(tableHeaderRow).Height = 24;
            ws.SheetView.FreezeRows(tableHeaderRow);
            ws.SheetView.FreezeColumns(2);
            ws.Columns(1, headers.Length).AdjustToContents(1, Math.Min(mainTableLastRow, tableHeaderRow + 500));
            ApplyGhafReportLayout(ws, headers.Length, mainTableLastRow);

            wb.SaveAs(filePath);
            UpdateStage("Saving report", 95, exportRows.Count, exportRows.Count, "Workbook saved. Finalizing report record.");

            report.Status = "Completed";
            report.GeneratedAt = DateTime.UtcNow;
            report.FilePath = $"/reports/{fileName}";

            // Send email if recipients were specified
            if (!string.IsNullOrWhiteSpace(report.EmailTo))
            {
                try
                {
                    UpdateStage("Sending email", 98, exportRows.Count, exportRows.Count, $"Sending report to {report.EmailTo}.");
                    await _emailService.SendReportAsync(report.EmailTo, report.ReportId, report.ReportType, filePath);
                }
                catch (Exception emailEx)
                {
                    _logger.LogWarning(emailEx, "Report {ReportId} generated but email delivery failed.", report.ReportId);
                }
            }

            ReportGenerationState.Finish($"Report {report.ReportId} completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate report {ReportId}", report.ReportId);
            report.Status = "Failed";
            ReportGenerationState.Fail($"Report {report.ReportId} failed: {ex.Message}");
        }

        await _context.SaveChangesAsync();
    }

    private void ApplyGhafReportHeader(IXLWorksheet ws, int lastColumn, ReportRequest report, int rowCount, int unmatchedRemittanceCount)
    {
        var title = GetReportTitle(report.ReportType);
        var generatedLocal = DateTime.Now;
        var period = $"{report.DateFrom:dd MMM yyyy} - {report.DateTo:dd MMM yyyy}";
        var facility = report.Branch?.Name ?? "All Facilities";

        ws.Range(1, 1, 6, lastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml(GhafCream);
        ws.Range(1, 1, 6, lastColumn).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        ws.Range(1, 1, 6, lastColumn).Style.Border.BottomBorderColor = XLColor.FromHtml(GhafBorder);

        ws.Range(1, 1, 6, 1).Style.Fill.BackgroundColor = XLColor.FromHtml(GhafTeal);
        ws.Range(1, 2, 6, 6).Merge();
        ws.Range(1, 7, 1, lastColumn).Merge();
        ws.Range(2, 7, 2, lastColumn).Merge();
        ws.Range(3, 7, 3, lastColumn).Merge();

        var logoPath = ResolveReportLogoPath();
        if (!string.IsNullOrWhiteSpace(logoPath))
        {
            try
            {
                var picture = ws.AddPicture(logoPath)
                    .MoveTo(ws.Cell(1, 2));
                picture.Width = 92;
                picture.Height = 120;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not add Ghaf report logo to workbook.");
                WriteTextFallbackLogo(ws);
            }
        }
        else
        {
            WriteTextFallbackLogo(ws);
        }

        ws.Cell(1, 7).Value = "GHAF BUSINESS SERVICES";
        ws.Cell(1, 7).Style.Font.FontColor = XLColor.FromHtml(GhafTeal);
        ws.Cell(1, 7).Style.Font.Bold = true;
        ws.Cell(1, 7).Style.Font.FontSize = 10;
        ws.Cell(1, 7).Style.Alignment.Vertical = XLAlignmentVerticalValues.Bottom;

        ws.Cell(2, 7).Value = title;
        ws.Cell(2, 7).Style.Font.FontColor = XLColor.FromHtml(GhafInk);
        ws.Cell(2, 7).Style.Font.Bold = true;
        ws.Cell(2, 7).Style.Font.FontSize = 22;

        ws.Cell(3, 7).Value = "Healthcare revenue cycle intelligence";
        ws.Cell(3, 7).Style.Font.FontColor = XLColor.FromHtml(GhafPrimary);
        ws.Cell(3, 7).Style.Font.FontSize = 11;

        AddReportMeta(ws, 5, 7, "Facility", facility);
        AddReportMeta(ws, 5, 11, "Date Range", period);
        AddReportMeta(ws, 5, 16, "Rows", rowCount.ToString("N0", CultureInfo.InvariantCulture));
        AddReportMeta(ws, 5, 20, "Generated", generatedLocal.ToString("dd MMM yyyy HH:mm"));
        AddReportMeta(ws, 5, 26, "Report ID", report.ReportId);

        if (unmatchedRemittanceCount > 0)
            AddReportMeta(ws, 5, 33, "Ledger Notes", unmatchedRemittanceCount.ToString("N0", CultureInfo.InvariantCulture));

        ws.Row(1).Height = 20;
        ws.Row(2).Height = 28;
        ws.Row(3).Height = 20;
        ws.Row(4).Height = 8;
        ws.Row(5).Height = 28;
        ws.Row(6).Height = 8;
    }

    private void AddReportMeta(IXLWorksheet ws, int row, int column, string label, string value)
    {
        ws.Cell(row, column).Value = label;
        ws.Cell(row, column).Style.Font.FontSize = 8;
        ws.Cell(row, column).Style.Font.Bold = true;
        ws.Cell(row, column).Style.Font.FontColor = XLColor.FromHtml(GhafTeal);
        ws.Cell(row, column).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Cell(row, column + 1).Value = value;
        ws.Range(row, column + 1, row, column + 2).Merge();
        ws.Range(row, column + 1, row, column + 2).Style.Font.FontSize = 9;
        ws.Range(row, column + 1, row, column + 2).Style.Font.FontColor = XLColor.FromHtml(GhafInk);
        ws.Range(row, column + 1, row, column + 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private void ApplyGhafReportLayout(IXLWorksheet ws, int lastColumn, int lastTableRow)
    {
        ws.Range(1, 1, Math.Max(lastTableRow, 8), lastColumn).Style.Font.FontName = "Inter";
        ws.Range(8, 1, Math.Max(lastTableRow, 8), lastColumn).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Range(8, 1, Math.Max(lastTableRow, 8), lastColumn).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        ws.Range(8, 1, Math.Max(lastTableRow, 8), lastColumn).Style.Border.InsideBorderColor = XLColor.FromHtml("#D9EFEA");
        ws.Range(8, 1, Math.Max(lastTableRow, 8), lastColumn).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(8, 1, Math.Max(lastTableRow, 8), lastColumn).Style.Border.OutsideBorderColor = XLColor.FromHtml(GhafBorder);

        ws.Columns(1, lastColumn).Style.Alignment.WrapText = false;
        ws.Columns(31, 35).Style.Alignment.WrapText = true;
        ws.Column(2).Style.Font.FontColor = XLColor.FromHtml(GhafPrimary);
        ws.Column(2).Style.Font.Bold = true;
        ws.Column(16).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(25).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.Top = 0.35;
        ws.PageSetup.Margins.Bottom = 0.35;
        ws.PageSetup.Margins.Left = 0.25;
        ws.PageSetup.Margins.Right = 0.25;
        ws.SheetView.ZoomScale = 90;

        for (var c = 1; c <= lastColumn; c++)
        {
            var width = ws.Column(c).Width;
            if (width > 34)
                ws.Column(c).Width = 34;
            if (width < 9)
                ws.Column(c).Width = 9;
        }

        ws.Column(2).Width = 26;
        ws.Column(6).Width = 28;
        ws.Column(27).Width = 30;
        ws.Column(31).Width = 36;
        ws.Column(32).Width = 36;
        ws.Column(35).Width = 32;
    }

    private string? ResolveReportLogoPath()
    {
        var candidates = new[]
        {
            Path.Combine(_env.WebRootPath, "images", "ghaf-logo-primary-006884.png"),
            Path.Combine(_env.WebRootPath, "images", "ghaf-logo-soft-78C2C2.png"),
            Path.Combine(_env.WebRootPath, "images", "ghaf-report-logo-teal.png"),
            "/Users/jawaa/Downloads/Ghaf Business Services Website/src/imports/ghaf-logo-exact-teal.png",
            "/Users/jawaa/Downloads/Ghaf Business Services Website/src/imports/ghaf-logo-lockup-exact-teal.png",
            "/Users/jawaa/Library/CloudStorage/OneDrive-GhafBusinessServices/Ghaf Docs/Ghaf Logo !/ghaf logo/Working file/resized/ghaf-logo-max-2048.jpg",
            "/Users/jawaa/Downloads/Ghaf Business Services Website/src/imports/ghaf-logo.png"
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    private static void WriteTextFallbackLogo(IXLWorksheet ws)
    {
        ws.Cell(2, 2).Value = "GHAF";
        ws.Cell(2, 2).Style.Font.Bold = true;
        ws.Cell(2, 2).Style.Font.FontSize = 20;
        ws.Cell(2, 2).Style.Font.FontColor = XLColor.FromHtml(GhafTeal);
        ws.Cell(3, 2).Value = "BUSINESS SERVICES";
        ws.Cell(3, 2).Style.Font.FontSize = 8;
        ws.Cell(3, 2).Style.Font.FontColor = XLColor.FromHtml(GhafPrimary);
    }

    private static string GetReportTitle(string reportType) => reportType switch
    {
        "ClaimSummary" => "Claim Summary Report",
        "ClaimActivity" => "Claim Activity Report",
        "RemittanceActivity" => "Remittance Activity Report",
        "ClaimReceiver" => "Claim Receiver Report",
        "ClaimClinician" => "Claim Clinician Report",
        "FinanceTAT" => "Finance TAT Report",
        "DenialReport" => "Denial Query Report",
        "ClaimLifeCycle" => "Claim Life Cycle Report",
        _ => "Ghaf Business Intelligence Report"
    };

    // ── XML parsers ────────────────────────────────────────────────────

    private static ClaimRow MapParsedSubmission(
        XmlParsedRecord record,
        string facilityName,
        IReadOnlyDictionary<string, string> payerLookup)
    {
        var receiverId = record.ReceiverId ?? "";
        var payerId = record.PayerId ?? "";

        return new ClaimRow
        {
            Facility = facilityName,
            ClaimId = record.ClaimId,
            ReceiverId = receiverId,
            ReceiverName = string.IsNullOrWhiteSpace(record.ReceiverName)
                ? ResolveLookupName(receiverId, payerLookup)
                : record.ReceiverName,
            PayerId = payerId,
            PayerName = string.IsNullOrWhiteSpace(record.PayerName)
                ? ResolveLookupName(payerId, payerLookup)
                : record.PayerName,
            PatientId = record.PatientId ?? "",
            MemberId = record.MemberId ?? "",
            TreatmentDate = record.TreatmentDate ?? "",
            TreatmentDateEnd = record.TreatmentDateEnd ?? "",
            DateOfAdmission = record.DateOfAdmission ?? "",
            SubmissionDate = record.SubmissionDate ?? record.TransactionDate ?? "",
            EncounterType = record.EncounterType ?? "",
            Clinician = record.Clinician ?? "",
            ServiceYear = record.ServiceYear ?? "",
            ServiceMonth = record.ServiceMonth ?? "",
            SubmissionLevel = "Initial",
            NetAmtInitial = record.NetAmount,
            IdPayer = record.IdPayer ?? "",
            SubmissionFile = record.FileName ?? record.FileId ?? "",
            ResubmissionType = record.ResubmissionType ?? "",
            PrincipalDiagnosis = record.PrincipalDiagnosis ?? ""
        };
    }

    private static IEnumerable<ClaimRow> ParseClaimXml(
        string xml, string? fileId, string? fileName, string? txDate, string facilityName,
        IReadOnlyDictionary<string, string> payerLookup)
    {
        if (string.IsNullOrEmpty(xml)) yield break;
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { yield break; }

        if (!string.Equals(doc.Root?.Name.LocalName, "Claim.Submission", StringComparison.OrdinalIgnoreCase))
            yield break;

        var header = doc.Root?.Element("Header");
        var receiverId = header?.Element("ReceiverID")?.Value ?? "";
        var submDate = header?.Element("TransactionDate")?.Value ?? txDate ?? "";

        if (receiverId.StartsWith("DHA-F-", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (var claim in doc.Descendants("Claim"))
        {
            var enc = claim.Element("Encounter");
            var treatStart = enc?.Element("Start")?.Value ?? "";
            var treatEnd = enc?.Element("End")?.Value ?? "";
            var encTypeRaw = enc?.Element("Type")?.Value ?? "";
            var encType = MapEncounterType(encTypeRaw);
            var clinician = claim.Descendants("Activity")
                                     .FirstOrDefault()?.Element("Clinician")?.Value ?? "";
            var principalDiag = claim.Elements("Diagnosis")
                                     .FirstOrDefault(d => d.Element("Type")?.Value == "Principal")
                                     ?.Element("Code")?.Value ?? "";
            var claimId = claim.Element("ID")?.Value ?? "";
            var receiverName = ResolveLookupName(receiverId, payerLookup);
            var payerId = claim.Element("PayerID")?.Value ?? "";
            var payerName = ResolveLookupName(payerId, payerLookup);
            var resubmissionType = claim.Element("Resubmission")?.Element("Type")?.Value
                                ?? claim.Descendants("Resubmission").FirstOrDefault()?.Element("Type")?.Value
                                ?? "";

            var serviceYear = "";
            var serviceMonth = "";
            var admDate = "";
            if (DateTime.TryParseExact(treatStart, "dd/MM/yyyy HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var td))
            {
                serviceYear = td.Year.ToString();
                serviceMonth = td.ToString("MMMM");
                if (encTypeRaw == "2") admDate = treatStart; // inpatient only
            }

            decimal.TryParse(claim.Element("Net")?.Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out var net);

            yield return new ClaimRow
            {
                Facility = facilityName,
                ClaimId = claimId,
                ReceiverId = receiverId,
                ReceiverName = receiverName,
                PayerId = payerId,
                PayerName = payerName,
                PatientId = enc?.Element("PatientID")?.Value ?? "",
                MemberId = claim.Element("MemberID")?.Value ?? "",
                TreatmentDate = treatStart,
                TreatmentDateEnd = treatEnd,
                DateOfAdmission = admDate,
                SubmissionDate = submDate,
                EncounterType = encType,
                Clinician = clinician,
                ServiceYear = serviceYear,
                ServiceMonth = serviceMonth,
                SubmissionLevel = "Initial",
                NetAmtInitial = net,
                IdPayer = claim.Element("IDPayer")?.Value ?? "",
                SubmissionFile = fileName ?? fileId ?? "",
                ResubmissionType = resubmissionType,
                PrincipalDiagnosis = principalDiag
            };
        }
    }

    private static IEnumerable<RaEntry> ParseRaXml(string xml, string? fileName, string? txDate)
    {
        if (string.IsNullOrEmpty(xml)) yield break;
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { yield break; }

        if (!string.Equals(doc.Root?.Name.LocalName, "Remittance.Advice", StringComparison.OrdinalIgnoreCase))
            yield break;

        static string? ChildValue(XElement element, string localName) =>
            element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

        var header = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
        var raDate = header == null ? txDate ?? "" : ChildValue(header, "TransactionDate") ?? txDate ?? "";
        var headerPayRef = header == null ? "" : ChildValue(header, "PaymentReference") ?? "";

        foreach (var claim in doc.Descendants().Where(e => e.Name.LocalName == "Claim"))
        {
            var claimId = ChildValue(claim, "ID") ?? ChildValue(claim, "ClaimID") ?? "";
            if (string.IsNullOrWhiteSpace(claimId)) continue;

            decimal received = 0m;
            decimal approved = 0m;
            var denialCodes = new List<string>();
            var denialDescriptions = new List<string>();

            foreach (var activity in claim.Descendants().Where(e => e.Name.LocalName == "Activity"))
            {
                if (decimal.TryParse(ChildValue(activity, "Net"), NumberStyles.Any, CultureInfo.InvariantCulture, out var net))
                    received += net;

                if (decimal.TryParse(ChildValue(activity, "PaymentAmount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var payment))
                    approved += payment;

                var denialCode = ChildValue(activity, "DenialCode");
                if (!string.IsNullOrWhiteSpace(denialCode) && !denialCodes.Contains(denialCode, StringComparer.OrdinalIgnoreCase))
                    denialCodes.Add(denialCode);
            }

            foreach (var denial in claim.Descendants().Where(e => e.Name.LocalName == "Denial"))
            {
                var denialCode = ChildValue(denial, "Code");
                if (!string.IsNullOrWhiteSpace(denialCode) && !denialCodes.Contains(denialCode, StringComparer.OrdinalIgnoreCase))
                    denialCodes.Add(denialCode);

                var denialDesc = ChildValue(denial, "Description");
                if (!string.IsNullOrWhiteSpace(denialDesc))
                    denialDescriptions.Add(denialDesc);
            }

            var claimComments = ChildValue(claim, "Comments");
            if (!string.IsNullOrWhiteSpace(claimComments))
                denialDescriptions.Add(claimComments);

            var settlementDate = ChildValue(claim, "DateSettlement") ?? raDate;
            var payRef = ChildValue(claim, "PaymentReference") ?? headerPayRef;

            yield return new RaEntry
            {
                ClaimId = claimId,
                ApprovedAmt = approved,
                ReceivedAmt = received,
                RaFile = fileName ?? "",
                RaDate = raDate,
                SettlementDate = settlementDate,
                PaymentRef = payRef,
                DenialCode = string.Join(" | ", denialCodes),
                DenialDescription = string.Join(" | ", denialDescriptions.Distinct(StringComparer.OrdinalIgnoreCase)),
                Status = approved <= 0 ? "Rejected" : "Paid"
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
        _ => code ?? ""
    };

    private static string ExtractFirstDenialCode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            var codes = JsonSerializer.Deserialize<List<string>>(json);
            return codes?.FirstOrDefault() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadPayerLookupAsync()
    {
        var rows = await _context.DhpoCodingSets
            .AsNoTracking()
            .Where(x => x.Category == "Payer")
            .Select(x => new { x.Code, x.Name })
            .ToListAsync();

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name))
                continue;

            lookup[row.Code.Trim()] = row.Name.Trim();
        }

        return lookup;
    }

    private static string ResolveLookupName(string code, IReadOnlyDictionary<string, string> lookup)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";
        return lookup.TryGetValue(code.Trim(), out var name) ? name : code.Trim();
    }

    private static bool IsResubmissionRow(ClaimRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ResubmissionType))
            return true;

        return row.SubmissionFile.StartsWith("RES-", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, ResubmissionAggregate> AggregateResubmissions(IEnumerable<ClaimRow> rows)
    {
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.ClaimId))
            .GroupBy(r => r.ClaimId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new ResubmissionAggregate
                {
                    ClaimId = g.Key,
                    Count = g.Count(),
                    NetAmount = g.Sum(r => r.NetAmtInitial),
                    Files = string.Join(" | ", g.Select(r => r.SubmissionFile)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                    Types = string.Join(" | ", g.Select(r => r.ResubmissionType)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static string DetermineSubmissionLevel(int outboundCount, int inboundCount, IEnumerable<string> resubmissionTypes)
    {
        var type = resubmissionTypes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .FirstOrDefault();

        if (outboundCount <= 1)
        {
            if (inboundCount > 0)
                return $"{inboundCount} RA received";
            return "Initial Submission";
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            if (type.Equals("correction", StringComparison.OrdinalIgnoreCase))
                return "Resubmitted - Correction";
            if (type.Equals("internal complaint", StringComparison.OrdinalIgnoreCase))
                return "Resubmitted - Internal Complaint";
            return $"Resubmitted - {type}";
        }

        return "Resubmitted";
    }

    private static bool IsRemittanceWithinReportRange(RemittanceClaimRow row, DateTime from, DateTime to)
    {
        var remittanceDate = ParseDhpoDate(row.SettlementDate) ?? ParseDhpoDate(row.TransactionDate);
        return !remittanceDate.HasValue
            || (remittanceDate.Value.Date >= from.Date && remittanceDate.Value.Date <= to.Date);
    }

    private static Dictionary<string, RaEntry> AggregateRemittances(IEnumerable<RemittanceClaimRow> remittanceClaims)
    {
        return remittanceClaims
            .Where(x => !string.IsNullOrWhiteSpace(x.ClaimId))
            .GroupBy(x => x.ClaimId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var paid = g.Sum(x => x.PaidAmount ?? 0m);
                    var received = g.Sum(x => x.OriginalAmount ?? 0m);

                    var settlementDates = g
                        .Select(x => x.SettlementDate ?? x.TransactionDate ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var parsedDates = settlementDates
                        .Select(ParseDhpoDate)
                        .Where(d => d.HasValue)
                        .Select(d => d!.Value)
                        .ToList();

                    var fileNames = g.Select(x => x.FileName ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var paymentRefs = g.Select(x => x.PaymentReference ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var denialCodes = g.Select(x => ExtractFirstDenialCode(x.DenialCodesJson))
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var denialDescriptions = g.Select(x => x.Comments ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    return new RaEntry
                    {
                        ClaimId = g.Key,
                        ApprovedAmt = paid,
                        ReceivedAmt = received,
                        RaFile = string.Join(" | ", fileNames),
                        RaDate = string.Join(" | ", settlementDates),
                        SettlementDate = string.Join(" | ", settlementDates),
                        SettlementDateValue = parsedDates.OrderByDescending(d => d).FirstOrDefault(),
                        PaymentRef = string.Join(" | ", paymentRefs),
                        DenialCode = string.Join(" | ", denialCodes),
                        DenialDescription = string.Join(" | ", denialDescriptions),
                        Status = paid <= 0 ? "Rejected" : "Paid"
                    };
                },
                StringComparer.OrdinalIgnoreCase);
    }

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
        public int OutboundCount { get; set; }
        public int InboundCount { get; set; }
        public int RecordCount { get; set; }
        public decimal NetAmtInitial { get; set; }
        public decimal NetAmtResubmission { get; set; }
        public string IdPayer { get; set; } = "";
        public string SubmissionFile { get; set; } = "";
        public string ResubmissionFile { get; set; } = "";
        public string RaFile { get; set; } = "";
        public string RaDate { get; set; } = "";
        public string ResubmissionType { get; set; } = "";
        public string PrincipalDiagnosis { get; set; } = "";
        public RaEntry? Ra { get; set; }
    }

    private class ResubmissionAggregate
    {
        public string ClaimId { get; set; } = "";
        public int Count { get; set; }
        public decimal NetAmount { get; set; }
        public string Files { get; set; } = "";
        public string Types { get; set; } = "";
    }

    private class RemittanceClaimRow
    {
        public string ClaimId { get; set; } = "";
        public decimal? PaidAmount { get; set; }
        public decimal? OriginalAmount { get; set; }
        public string? SettlementDate { get; set; }
        public string? PaymentReference { get; set; }
        public string? DenialCodesJson { get; set; }
        public string? Comments { get; set; }
        public string? FileName { get; set; }
        public string? TransactionDate { get; set; }
    }

    private class UnmatchedRemittanceRow
    {
        public string TransactionRef { get; set; } = "";
        public string RemittanceFileName { get; set; } = "";
    }

    private class RaEntry
    {
        public string ClaimId { get; set; } = "";
        public decimal? ApprovedAmt { get; set; }
        public decimal? ReceivedAmt { get; set; }
        public string RaFile { get; set; } = "";
        public string RaDate { get; set; } = "";
        public string SettlementDate { get; set; } = "";
        public DateTime? SettlementDateValue { get; set; }
        public string PaymentRef { get; set; } = "";
        public string DenialCode { get; set; } = "";
        public string DenialDescription { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
