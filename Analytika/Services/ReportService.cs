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
    private const string GhafInk = "#011C40";
    private const string GhafPrimary = "#A7EBF2";
    private const string GhafTeal = "#54ACBF";
    private const string GhafPale = "#26658C";
    private const string GhafCream = "#EAF4FB";
    private const string GhafBorder = "#35577D";

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
                    ReportGenerationState.Fail(request.Id, $"Report {request.ReportId} could not start.");
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

    public async Task<(List<ReportRequest> Reports, int Total)> GetReportsAsync(string reportType, int page, int pageSize, int? facilityId = null)
    {
        var query = _context.ReportRequests
            .Include(r => r.Branch)
            .Include(r => r.Receiver)
            .Include(r => r.Payer)
            .Include(r => r.Clinician)
            .Where(r => r.ReportType == reportType)
            .Where(r => facilityId == null || r.BranchId == facilityId)
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

    public async Task RunScheduledReportAsync(int scheduleId)
    {
        var schedule = await _context.ReportSchedules.FindAsync(scheduleId);
        if (schedule == null || !schedule.IsActive) return;

        var facilityIds = string.IsNullOrWhiteSpace(schedule.FacilityIdsJson)
            ? null
            : JsonSerializer.Deserialize<List<int>>(schedule.FacilityIdsJson);

        var request = new ReportRequest
        {
            ReportType = schedule.ReportType,
            BranchId = facilityIds?.FirstOrDefault(),
            DateFrom = DateTime.UtcNow.AddMonths(-1),
            DateTo = DateTime.UtcNow,
            FileFormat = schedule.FileFormat,
            EmailTo = schedule.Recipients
        };

        try
        {
            await QueueReportAsync(request);
            schedule.LastRunAt = DateTime.UtcNow;
            schedule.LastRunStatus = "OK";
        }
        catch (Exception ex)
        {
            schedule.LastRunAt = DateTime.UtcNow;
            schedule.LastRunStatus = $"Error: {ex.Message}";
        }

        await _context.SaveChangesAsync();
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

            var reportsDir = GetReportsDirectory();
            Directory.CreateDirectory(reportsDir);

            var fileName = $"{report.ReportId}_{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx";
            var filePath = Path.Combine(reportsDir, fileName);

            void UpdateStage(string stage, int pct, int done = 0, int total = 0, string? message = null)
                => ReportGenerationState.Update(report.Id, stage, pct, done, total, message);

            // Facility scope: multi-select JSON is the source of truth; empty = all facilities.
            var facilityIds = ResolveFacilityIds(report);

            // ── Dispatch: report types that use a purpose-built layout instead of
            //    the matched-claim workbook fall out here and finalise on their own. ──
            if (report.ReportType == "SubmissionXML")
            {
                // Fast, claim-level submissions from the slim XmlParsedRecords cache.
                await GenerateSubmissionClaimsWorkbookAsync(report, filePath, facilityIds, UpdateStage);
                await FinalizeReportAsync(report, fileName, filePath, UpdateStage);
                return;
            }
            if (report.ReportType == "LiveSubmission")
            {
                // File-level submission metadata straight from PortalTransactions
                // (the "live" downloaded files). Backed by the covering index.
                await GenerateSubmissionXmlWorkbookAsync(report, filePath, facilityIds, UpdateStage);
                await FinalizeReportAsync(report, fileName, filePath, UpdateStage);
                return;
            }
            if (report.ReportType is "RemittanceActivity" or "DenialReport")
            {
                await GenerateRemittanceWorkbookAsync(report, filePath, facilityIds,
                    denialsOnly: report.ReportType == "DenialReport", UpdateStage);
                await FinalizeReportAsync(report, fileName, filePath, UpdateStage);
                return;
            }

            UpdateStage("Preparing query plan", 3, 0, 0, $"ReportRequests #{report.Id}: facility={report.Branch?.Name ?? "All"}, range={report.DateFrom:dd/MM/yyyy}-{report.DateTo:dd/MM/yyyy}.");
            UpdateStage("Preparing parsed XML", 5, 0, 0, "Checking claim-level XML cache before report matching.");
            // Reports ALWAYS use the prepared parsed-XML cache and match in-memory
            // by ClaimId. Neither raw-XML parsing NOR MatchParsedRecordsAsync runs
            // here anymore: the latter rewrote IsMatched across the ENTIRE 776k-row
            // table (two full-table UPDATEs incl. a correlated subquery) on every
            // report, holding the SQLite write lock for minutes and starving all
            // other writes — while its result only fed a progress message. The
            // IsMatched flags belong to Portal > XML Parsing maintenance flows.
            await _xmlParsingService.EnsureSchemaAsync();
            var parseResult = new XmlParsingRunResult();
            UpdateStage("Preparing parsed XML", 20, 0, 0, "Using prepared XML cache. Prepare or rebuild from Portal > XML Parsing when new files are downloaded.");
            UpdateStage("Preparing parsed XML", 20, parseResult.RecordsSaved, parseResult.FilesScanned,
                $"XML cache ready: {parseResult.RecordsSaved:N0} new claim row(s), {parseResult.MatchedClaimRefs:N0} matched claim ref(s).");

            UpdateStage("Loading payer lookup", 18, 0, 0, "Query: DhpoCodingSets where Category = Payer.");
            var payerLookup = await LoadPayerLookupAsync();

            // ── Load parsed outbound claim submissions ─────────────────
            UpdateStage("Querying parsed submissions", 25, 0, 0, "Query: XmlParsedRecords where RecordKind = Submission and ReadyForReport = true.");
            var parsedClaimQuery = _context.XmlParsedRecords
                .AsNoTracking()
                .Where(r => r.ReadyForReport && r.RecordKind == "Submission");

            if (facilityIds.Count > 0)
                parsedClaimQuery = parsedClaimQuery.Where(r => facilityIds.Contains(r.FacilityId));

            var parsedSubmissions = await parsedClaimQuery
                .OrderBy(r => r.ParsedAt)
                .ToListAsync();
            UpdateStage("Loading parsed submissions", 35, parsedSubmissions.Count, parsedSubmissions.Count, $"Loaded {parsedSubmissions.Count:N0} parsed submission claim row(s).");

            // ── Load parsed remittance rows and build a claim-id lookup ──
            UpdateStage("Querying parsed remittances", 42, 0, 0, "Query: XmlParsedRecords where RecordKind = Remittance and ReadyForReport = true.");
            var parsedRemittanceQuery = _context.XmlParsedRecords
                .AsNoTracking()
                .Where(r => r.ReadyForReport && r.RecordKind == "Remittance");

            if (facilityIds.Count > 0)
                parsedRemittanceQuery = parsedRemittanceQuery.Where(r => facilityIds.Contains(r.FacilityId));

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
                    TransactionDate = r.TransactionDate,
                    ClaimCategory = r.ClaimCategory
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
                    row.ClaimCategory = ra.ClaimCategory;
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
            var ws = wb.Worksheets.Add(GetWorksheetName(report.ReportType));
            const int tableHeaderRow = 8;

            var headers = new[]
            {
                "Facility", "TransactionRef", "Receiver", "Receiver Name",
                "Payer", "Payer Name", "Patient ID", "Member Id",
                "Treatment Date", "Date Of Admission", "Submission Date",
                "Encounter Type", "Clinician", "Service Year", "Service Month",
                "Record Count", "Submission Level",
                "Gross Amt", "Net Amt - Initial Sub", "RA Received Amt",
                "Net Amt - Resubmission", "Approved Amt",
                "Initial Sub Rejected Amt", "Rejected Amt - Resubmission",
                "Unsettled Amt", "Payment Status", "Claim Category", "Denial Code",
                "Denial Description", "Payment Ref", "Settlement Date",
                "ID Payer", "Submission File", "RA File", "RA Date", "TAT",
                "Principal Diagnosis", "All Diagnoses",
                "Patient Gender", "Patient DOB", "National ID"
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
                var hasResubmission = r.NetAmtResubmission > 0m
                    || r.SubmissionLevel.Contains("Resubmit", StringComparison.OrdinalIgnoreCase)
                    || r.SubmissionLevel.Contains("Recon", StringComparison.OrdinalIgnoreCase)
                    || r.SubmissionLevel.Contains("awaiting RA", StringComparison.OrdinalIgnoreCase);
                var payStatus = ra == null
                    ? (hasResubmission ? "Pending - Resubmitted" : "Pending")
                    : (approvedAmt <= 0 ? "Rejected" : approvedAmt < balanceAmount - 0.01m ? "Partial" : "Paid");

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
                ws.Cell(rn, 18).Value = r.GrossAmtInitial;
                ws.Cell(rn, 19).Value = netInitial;
                ws.Cell(rn, 20).Value = receivedAmt;
                ws.Cell(rn, 21).Value = netResubmission;
                ws.Cell(rn, 22).Value = approvedAmt;
                ws.Cell(rn, 23).Value = rejInitial;
                ws.Cell(rn, 24).Value = rejResubmission;
                ws.Cell(rn, 25).Value = unsettled;
                ws.Cell(rn, 26).Value = payStatus;
                ws.Cell(rn, 27).Value = r.ClaimCategory;
                ws.Cell(rn, 28).Value = ra?.DenialCode ?? "";
                ws.Cell(rn, 29).Value = ra?.DenialDescription ?? "";
                ws.Cell(rn, 30).Value = ra?.PaymentRef ?? "";
                ws.Cell(rn, 31).Value = ra?.SettlementDate ?? "";
                ws.Cell(rn, 32).Value = r.IdPayer;
                ws.Cell(rn, 33).Value = r.SubmissionFile;
                ws.Cell(rn, 34).Value = r.RaFile;
                ws.Cell(rn, 35).Value = r.RaDate;
                ws.Cell(rn, 36).Value = tatDays;
                ws.Cell(rn, 37).Value = r.PrincipalDiagnosis;
                ws.Cell(rn, 38).Value = r.DiagnosesDisplay;
                ws.Cell(rn, 39).Value = r.PatientGender;
                ws.Cell(rn, 40).Value = r.PatientDob;
                ws.Cell(rn, 41).Value = r.PatientNationalId;

                // Zebra stripe
                if (i % 2 == 1)
                    ws.Row(rn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7FCFA");

                // Amount columns format
                foreach (var col in new[] { 18, 19, 20, 21, 22, 23, 24, 25 })
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

            ReportGenerationState.Finish(report.Id, $"Report {report.ReportId} completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate report {ReportId}", report.ReportId);
            report.Status = "Failed";
            ReportGenerationState.Fail(report.Id, $"Report {report.ReportId} failed: {ex.Message}");
        }

        await _context.SaveChangesAsync();
    }

    // Reports are written to a persistent folder NEXT TO the database (the data dir),
    // NOT inside wwwroot — otherwise every `dotnet publish` redeploy wipes them and
    // downloads 404 with "File not found on server". Shared with the download path.
    public static string ResolveReportsDirectory(string? dbConnectionString, string fallbackWebRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(dbConnectionString))
            {
                var dbPath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(dbConnectionString).DataSource;
                if (!string.IsNullOrWhiteSpace(dbPath))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
                    if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "reports");
                }
            }
        }
        catch { }
        return Path.Combine(fallbackWebRoot, "reports");
    }

    private string GetReportsDirectory()
        => ResolveReportsDirectory(_context.Database.GetDbConnection().ConnectionString, _env.WebRootPath);

    // Facility scope from the multi-select JSON (source of truth); falls back to the
    // single BranchId. Empty list = all facilities.
    private static List<int> ResolveFacilityIds(ReportRequest report)
    {
        var ids = new List<int>();
        if (!string.IsNullOrWhiteSpace(report.FacilityIdsJson))
        {
            try { ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(report.FacilityIdsJson) ?? new(); }
            catch { }
        }
        if (ids.Count == 0 && report.BranchId.HasValue) ids.Add(report.BranchId.Value);
        return ids.Where(i => i > 0).Distinct().ToList();
    }

    private static HashSet<string> ResolveEncounterTypes(ReportRequest report)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(report.EncounterTypesJson))
        {
            try { foreach (var e in System.Text.Json.JsonSerializer.Deserialize<List<string>>(report.EncounterTypesJson) ?? new())
                    if (!string.IsNullOrWhiteSpace(e)) set.Add(e.Trim()); }
            catch { }
        }
        if (set.Count == 0 && !string.IsNullOrWhiteSpace(report.EncounterType)) set.Add(report.EncounterType.Trim());
        return set;
    }

    private async Task FinalizeReportAsync(ReportRequest report, string fileName, string filePath,
        Action<string, int, int, int, string?> updateStage)
    {
        report.Status = "Completed";
        report.GeneratedAt = DateTime.UtcNow;
        report.FilePath = $"/reports/{fileName}";
        await _context.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(report.EmailTo))
        {
            try
            {
                updateStage("Sending email", 98, 0, 0, $"Sending report to {report.EmailTo}.");
                await _emailService.SendReportAsync(report.EmailTo, report.ReportId, report.ReportType, filePath);
            }
            catch (Exception emailEx)
            {
                _logger.LogWarning(emailEx, "Report {ReportId} generated but email delivery failed.", report.ReportId);
            }
        }
        ReportGenerationState.Finish(report.Id, $"Report {report.ReportId} completed successfully.");
    }

    private void WriteReportHeaderRow(IXLWorksheet ws, string[] headers, int headerRow)
    {
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(GhafPrimary);
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
    }

    private void FinishReportSheet(IXLWorksheet ws, int headerRow, int columns, int dataRows)
    {
        var lastRow = headerRow + Math.Max(0, dataRows);
        ws.Range(headerRow, 1, lastRow, columns).SetAutoFilter();
        ws.Row(headerRow).Height = 24;
        ws.SheetView.FreezeRows(headerRow);
        ws.SheetView.FreezeColumns(2);
        ws.Columns(1, columns).AdjustToContents(1, Math.Min(lastRow, headerRow + 500));
        ApplyGhafReportLayout(ws, columns, lastRow);
    }

    // Slim projection for the remittance/denial report layouts.
    private sealed record RemitReportRow(
        int FacilityId, string ClaimId, string? PayerId, string? PayerName, string? Clinician,
        string? EncounterType, string? ServiceYear, string? ServiceMonth, decimal NetAmount,
        decimal PaidAmount, string? DenialCodesJson, string? PaymentReference,
        string? SettlementDate, string? TransactionDate, string? FileName);

    // ── Remittance Activity / Denial reports — sourced from the complete parsed
    //    remittance data in XmlParsedRecords (the RemittanceClaims table is only
    //    ~9% populated). Denial = Net > Paid, or a denial code is present. ──
    private async Task GenerateRemittanceWorkbookAsync(ReportRequest report, string filePath,
        List<int> facilityIds, bool denialsOnly, Action<string, int, int, int, string?> updateStage)
    {
        updateStage("Querying remittances", 20, 0, 0, "Query: XmlParsedRecords where RecordKind = Remittance.");
        var q = _context.XmlParsedRecords.AsNoTracking()
            .Where(r => r.ReadyForReport && r.RecordKind == "Remittance");
        if (facilityIds.Count > 0) q = q.Where(r => facilityIds.Contains(r.FacilityId));

        if (report.PayerId.HasValue)
        {
            var payer = report.Payer?.Name;
            if (!string.IsNullOrWhiteSpace(payer))
                q = q.Where(r => (r.PayerName != null && r.PayerName.Contains(payer))
                              || (r.PayerId != null && r.PayerId.Contains(payer)));
        }
        if (report.ClinicianId.HasValue)
        {
            var clin = report.Clinician?.Name;
            if (!string.IsNullOrWhiteSpace(clin))
                q = q.Where(r => r.Clinician != null && r.Clinician.Contains(clin));
        }

        // Date window applied IN SQL, before any cap. (A previous Take(200000)
        // before filtering silently dropped ~62k valid rows — caught by
        // tools/validate_remittance_report.py.)
        // The date column honours the report's Search Criteria. RA-related reports
        // DEFAULT to RA Date (the RA record's DHPO TransactionDate) but the user may
        // pick another header. All source columns are dd/MM/yyyy text; rows with a
        // missing/short value pass through.
        var df = report.DateFrom.ToString("yyyyMMdd");
        var dt = report.DateTo.ToString("yyyyMMdd");
        switch (report.SearchCriteria)
        {
            case "EncounterStartDate":
                q = q.Where(r => r.TreatmentDate == null || r.TreatmentDate.Length < 10
                    || (string.Compare(r.TreatmentDate.Substring(6, 4) + r.TreatmentDate.Substring(3, 2) + r.TreatmentDate.Substring(0, 2), df) >= 0
                     && string.Compare(r.TreatmentDate.Substring(6, 4) + r.TreatmentDate.Substring(3, 2) + r.TreatmentDate.Substring(0, 2), dt) <= 0));
                break;
            case "EncounterEndDate":
                q = q.Where(r => r.TreatmentDateEnd == null || r.TreatmentDateEnd.Length < 10
                    || (string.Compare(r.TreatmentDateEnd.Substring(6, 4) + r.TreatmentDateEnd.Substring(3, 2) + r.TreatmentDateEnd.Substring(0, 2), df) >= 0
                     && string.Compare(r.TreatmentDateEnd.Substring(6, 4) + r.TreatmentDateEnd.Substring(3, 2) + r.TreatmentDateEnd.Substring(0, 2), dt) <= 0));
                break;
            case "SubmissionDate":
                q = q.Where(r => r.SubmissionDate == null || r.SubmissionDate.Length < 10
                    || (string.Compare(r.SubmissionDate.Substring(6, 4) + r.SubmissionDate.Substring(3, 2) + r.SubmissionDate.Substring(0, 2), df) >= 0
                     && string.Compare(r.SubmissionDate.Substring(6, 4) + r.SubmissionDate.Substring(3, 2) + r.SubmissionDate.Substring(0, 2), dt) <= 0));
                break;
            case "SettlementDate":
                q = q.Where(r => r.SettlementDate == null || r.SettlementDate.Length < 10
                    || (string.Compare(r.SettlementDate.Substring(6, 4) + r.SettlementDate.Substring(3, 2) + r.SettlementDate.Substring(0, 2), df) >= 0
                     && string.Compare(r.SettlementDate.Substring(6, 4) + r.SettlementDate.Substring(3, 2) + r.SettlementDate.Substring(0, 2), dt) <= 0));
                break;
            default: // RemittanceDate (RA Date) — the default
                q = q.Where(r => r.TransactionDate == null || r.TransactionDate.Length < 10
                    || (string.Compare(r.TransactionDate.Substring(6, 4) + r.TransactionDate.Substring(3, 2) + r.TransactionDate.Substring(0, 2), df) >= 0
                     && string.Compare(r.TransactionDate.Substring(6, 4) + r.TransactionDate.Substring(3, 2) + r.TransactionDate.Substring(0, 2), dt) <= 0));
                break;
        }

        // Project only the columns this layout needs — the full entity carries
        // 40+ columns (diagnosis JSON, comments, patient fields) we never write.
        var list = await q.Take(500000)
            .Select(r => new RemitReportRow(
                r.FacilityId, r.ClaimId, r.PayerId, r.PayerName, r.Clinician,
                r.EncounterType, r.ServiceYear, r.ServiceMonth, r.NetAmount,
                r.PaidAmount, r.DenialCodesJson, r.PaymentReference,
                r.SettlementDate, r.TransactionDate, r.FileName))
            .ToListAsync();
        var facNames = await _context.Facilities.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name);
        var encTypes = ResolveEncounterTypes(report);

        var rows = new List<RemitReportRow>();
        foreach (var r in list)
        {
            // Date window already applied in SQL. Only encounter-type and
            // denials-only refinement remain in memory.
            if (encTypes.Count > 0 && (r.EncounterType == null
                || !encTypes.Any(e => r.EncounterType.Contains(e, StringComparison.OrdinalIgnoreCase))))
                continue;
            var denied = (r.NetAmount - r.PaidAmount) > 0.009m || HasDenialCodes(r.DenialCodesJson);
            if (denialsOnly && !denied) continue;
            rows.Add(r);
        }

        updateStage("Generating workbook", 80, rows.Count, rows.Count, $"Writing {rows.Count:N0} remittance row(s).");
        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Calibri";
        var ws = wb.Worksheets.Add(denialsOnly ? "Denial Activity" : "Remittance Activity");
        var headers = new[]
        {
            "Facility", "Claim ID", "Payer", "Payer Name", "Clinician", "Encounter Type",
            "Service Year", "Service Month", "Original (Net) Amt", "Paid Amt", "Denied Amt",
            "Denial Codes", "Payment Ref", "Settlement Date", "RA File"
        };
        const int cols = 15;

        // ── Executive header: title band, summary block, KPI cards. Data is
        //    untouched; this is presentation only. Table header lands at row 11. ──
        const string Navy = "#0A2540", Teal = "#0FB5AE", Slate = "#334155",
                     RedDk = "#B42318", RedBg = "#FEE4E2", AmberBg = "#FEF0C7",
                     Ink = "#0F172A", Muted = "#64748B", ZebraBg = "#F1F5F9", Line = "#E2E8F0";
        const int hr = 11;

        // Financial roll-ups (rounded consistently with row + totals math).
        decimal totOrig = rows.Sum(r => Math.Round(r.NetAmount, 2));
        decimal totPaid = rows.Sum(r => Math.Round(r.PaidAmount, 2));
        decimal totDenied = rows.Sum(r => Math.Max(0m, Math.Round(r.NetAmount, 2) - Math.Round(r.PaidAmount, 2)));
        double denialRate = totOrig != 0 ? (double)(totDenied / totOrig) : 0d;

        var facilityLabel = report.Branch?.Name ?? "All Facilities";
        var title = denialsOnly ? "REMITTANCE DENIAL REPORT" : "REMITTANCE ACTIVITY REPORT";

        // Title band (rows 1-3)
        ws.Range(1, 1, 3, cols).Style.Fill.BackgroundColor = XLColor.FromHtml(Navy);
        ws.Range(1, 1, 3, cols).Merge();
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 22;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(1, 1).Style.Alignment.Indent = 1;

        // Summary block (labels row 5, values row 6)
        void Summary(int col, int span, string label, string value)
        {
            ws.Cell(5, col).Value = label.ToUpperInvariant();
            ws.Cell(5, col).Style.Font.FontSize = 8;
            ws.Cell(5, col).Style.Font.Bold = true;
            ws.Cell(5, col).Style.Font.FontColor = XLColor.FromHtml(Muted);
            if (span > 1) ws.Range(6, col, 6, col + span - 1).Merge();
            ws.Cell(6, col).Value = value;
            ws.Cell(6, col).Style.Font.FontSize = 11;
            ws.Cell(6, col).Style.Font.Bold = true;
            ws.Cell(6, col).Style.Font.FontColor = XLColor.FromHtml(Ink);
        }
        Summary(1, 3, "Facility", facilityLabel);
        Summary(4, 3, "Reporting Period", $"{report.DateFrom:dd MMM yyyy} – {report.DateTo:dd MMM yyyy}");
        Summary(8, 1, "Rows", rows.Count.ToString("N0", CultureInfo.InvariantCulture));
        Summary(10, 3, "Generated", DateTime.Now.ToString("dd MMM yyyy HH:mm"));
        Summary(13, 3, "Report ID", report.ReportId);
        ws.Range(7, 1, 7, cols).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        ws.Range(7, 1, 7, cols).Style.Border.BottomBorderColor = XLColor.FromHtml(Line);

        // KPI cards (labels row 9, values row 10)
        void Kpi(int c1, int c2, string label, double value, string bg, bool percent = false)
        {
            ws.Range(9, c1, 10, c2).Style.Fill.BackgroundColor = XLColor.FromHtml(bg);
            ws.Range(9, c1, 9, c2).Merge();
            ws.Range(10, c1, 10, c2).Merge();
            ws.Cell(9, c1).Value = label.ToUpperInvariant();
            ws.Cell(9, c1).Style.Font.FontSize = 8;
            ws.Cell(9, c1).Style.Font.Bold = true;
            ws.Cell(9, c1).Style.Font.FontColor = XLColor.White;
            ws.Cell(9, c1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            ws.Cell(9, c1).Style.Alignment.Indent = 1;
            ws.Cell(10, c1).Value = value;
            ws.Cell(10, c1).Style.NumberFormat.Format = percent ? "0.0%" : "#,##0.00";
            ws.Cell(10, c1).Style.Font.FontSize = 15;
            ws.Cell(10, c1).Style.Font.Bold = true;
            ws.Cell(10, c1).Style.Font.FontColor = XLColor.White;
            ws.Cell(10, c1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            ws.Cell(10, c1).Style.Alignment.Indent = 1;
        }
        Kpi(1, 4, "Total Original Amount", (double)totOrig, Navy);
        Kpi(5, 8, "Total Paid Amount", (double)totPaid, Teal);
        Kpi(9, 11, "Total Denied Amount", (double)totDenied, RedDk);
        Kpi(12, 15, "Denial Rate", denialRate, Slate, percent: true);
        ws.Row(9).Height = 16;
        ws.Row(10).Height = 24;

        // ── Table header + data ──
        WriteReportHeaderRow(ws, headers, hr);
        // Fill blank descriptive fields with an en-dash placeholder for clean
        // scanning. Identity columns (Facility, Claim ID) and amounts are never
        // dashed; the validation agent treats "—" as blank.
        static string D(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s!;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var rn = hr + 1 + i;
            // Money math: round each amount to 2dp FIRST, then derive Denied —
            // raw REAL-backed doubles subtracted before rounding produced
            // off-by-0.01 denied values (caught by the validation agent).
            var net = Math.Round(r.NetAmount, 2);
            var paid = Math.Round(r.PaidAmount, 2);
            var denied = Math.Max(0m, net - paid);
            ws.Cell(rn, 1).Value = facNames.TryGetValue(r.FacilityId, out var fn) ? fn : $"Facility {r.FacilityId}";
            ws.Cell(rn, 2).Value = r.ClaimId;
            ws.Cell(rn, 3).Value = D(r.PayerId);
            ws.Cell(rn, 4).Value = D(r.PayerName);
            ws.Cell(rn, 5).Value = D(r.Clinician);
            ws.Cell(rn, 6).Value = D(r.EncounterType);
            ws.Cell(rn, 7).Value = D(r.ServiceYear);
            ws.Cell(rn, 8).Value = D(r.ServiceMonth);
            ws.Cell(rn, 9).Value = net;
            ws.Cell(rn, 10).Value = paid;
            ws.Cell(rn, 11).Value = denied;
            var codes = FormatDenialCodes(r.DenialCodesJson);
            ws.Cell(rn, 12).Value = D(codes);
            // Some source PaymentReference values carry a leading apostrophe
            // (an Excel text-marker artifact in the portal data). Strip it.
            ws.Cell(rn, 13).Value = D((r.PaymentReference ?? "").TrimStart('\''));
            ws.Cell(rn, 14).Value = D(r.SettlementDate);
            ws.Cell(rn, 15).Value = D(r.FileName);
            foreach (var col in new[] { 9, 10, 11 })
            {
                ws.Cell(rn, col).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(rn, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            // Zebra striping for scan-ability
            if (i % 2 == 1) ws.Range(rn, 1, rn, cols).Style.Fill.BackgroundColor = XLColor.FromHtml(ZebraBg);
            // Conditional emphasis: denied amount + denial codes
            if (denied > 0.009m)
            {
                ws.Cell(rn, 11).Style.Font.Bold = true;
                ws.Cell(rn, 11).Style.Font.FontColor = XLColor.FromHtml(RedDk);
                ws.Cell(rn, 11).Style.Fill.BackgroundColor = XLColor.FromHtml(RedBg);
            }
            if (!string.IsNullOrEmpty(codes))
            {
                ws.Cell(rn, 12).Style.Fill.BackgroundColor = XLColor.FromHtml(AmberBg);
                ws.Cell(rn, 12).Style.Font.FontColor = XLColor.FromHtml(RedDk);
                ws.Cell(rn, 12).Style.Font.Bold = true;
            }
        }

        if (rows.Count == 0)
        {
            var mr = hr + 1;
            ws.Range(mr, 1, mr, cols).Merge();
            ws.Cell(mr, 1).Value = "No data available for the selected filters.";
            ws.Cell(mr, 1).Style.Font.Italic = true;
            ws.Cell(mr, 1).Style.Font.FontColor = XLColor.FromHtml("#8A6D3B");
            ws.Cell(mr, 1).Style.Fill.BackgroundColor = XLColor.FromHtml(AmberBg);
            ws.Cell(mr, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Row(mr).Height = 28;
        }
        else
        {
            // Totals row: financial sums must reconcile against the database source.
            var tr = hr + rows.Count + 1;
            ws.Range(tr, 1, tr, cols).Style.Fill.BackgroundColor = XLColor.FromHtml(Navy);
            ws.Range(tr, 1, tr, cols).Style.Font.FontColor = XLColor.White;
            ws.Cell(tr, 1).Value = "TOTALS";
            ws.Cell(tr, 1).Style.Font.Bold = true;
            ws.Cell(tr, 8).Value = $"{rows.Count:N0} rows";
            ws.Cell(tr, 8).Style.Font.Bold = true;
            ws.Cell(tr, 9).Value = totOrig;
            ws.Cell(tr, 10).Value = totPaid;
            ws.Cell(tr, 11).Value = totDenied;
            foreach (var col in new[] { 9, 10, 11 })
            {
                ws.Cell(tr, col).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(tr, col).Style.Font.Bold = true;
                ws.Cell(tr, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            ws.Range(tr, 1, tr, cols).Style.Border.TopBorder = XLBorderStyleValues.Medium;
        }

        FinishReportSheet(ws, hr, cols, rows.Count);

        // Readable, capped column widths (override autofit where it runs wide)
        double[] widths = { 24, 16, 10, 26, 22, 14, 11, 12, 16, 16, 16, 20, 20, 18, 40 };
        for (int c = 0; c < cols; c++) ws.Column(c + 1).Width = widths[c];

        // Print-friendly: landscape, fit width, repeat title+header, margins, footer.
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.Top = 0.5; ws.PageSetup.Margins.Bottom = 0.6;
        ws.PageSetup.Margins.Left = 0.4; ws.PageSetup.Margins.Right = 0.4;
        ws.PageSetup.SetRowsToRepeatAtTop(1, hr);
        ws.PageSetup.Footer.Left.AddText(title + " · " + report.ReportId, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText("Page ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(" of ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);

        wb.SaveAs(filePath);
    }

    // ── Submission report (FAST) — claim-level submissions from the slim
    //    XmlParsedRecords cache. No blob table, so it runs in seconds. ──
    private async Task GenerateSubmissionClaimsWorkbookAsync(ReportRequest report, string filePath,
        List<int> facilityIds, Action<string, int, int, int, string?> updateStage)
    {
        updateStage("Querying submissions", 20, 0, 0, "Query: XmlParsedRecords where RecordKind = Submission.");
        var q = _context.XmlParsedRecords.AsNoTracking()
            .Where(r => r.ReadyForReport && r.RecordKind == "Submission");
        if (facilityIds.Count > 0) q = q.Where(r => facilityIds.Contains(r.FacilityId));

        // Date window in SQL on the submission's DHPO TransactionDate (dd/MM/yyyy);
        // rows with a missing/short value pass through.
        var df = report.DateFrom.ToString("yyyyMMdd");
        var dt = report.DateTo.ToString("yyyyMMdd");
        q = q.Where(r => r.TransactionDate == null || r.TransactionDate.Length < 10
            || (string.Compare(r.TransactionDate.Substring(6, 4) + r.TransactionDate.Substring(3, 2) + r.TransactionDate.Substring(0, 2), df) >= 0
             && string.Compare(r.TransactionDate.Substring(6, 4) + r.TransactionDate.Substring(3, 2) + r.TransactionDate.Substring(0, 2), dt) <= 0));

        var list = await q.Take(500000)
            .Select(r => new
            {
                r.FacilityId, r.ClaimId, r.PayerId, r.PayerName, r.Clinician, r.EncounterType,
                r.ServiceYear, r.ServiceMonth, r.NetAmount, r.SubmissionDate, r.TransactionDate, r.FileName
            })
            .ToListAsync();
        var facNames = await _context.Facilities.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name);

        updateStage("Generating workbook", 80, list.Count, list.Count, $"Writing {list.Count:N0} submission row(s).");
        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Calibri";
        var ws = wb.Worksheets.Add("Submissions");
        const int hr = 8;
        var headers = new[]
        {
            "Facility", "Claim ID", "Payer", "Payer Name", "Clinician", "Encounter Type",
            "Service Year", "Service Month", "Net (Billed) Amt", "Submission Date", "Transaction Date", "Source File"
        };
        ApplyGhafReportHeader(ws, headers.Length, report, list.Count, 0);
        WriteReportHeaderRow(ws, headers, hr);
        static string D(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s!;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i]; var rn = hr + 1 + i;
            ws.Cell(rn, 1).Value = facNames.TryGetValue(r.FacilityId, out var fn) ? fn : $"Facility {r.FacilityId}";
            ws.Cell(rn, 2).Value = r.ClaimId;
            ws.Cell(rn, 3).Value = D(r.PayerId);
            ws.Cell(rn, 4).Value = D(r.PayerName);
            ws.Cell(rn, 5).Value = D(r.Clinician);
            ws.Cell(rn, 6).Value = D(r.EncounterType);
            ws.Cell(rn, 7).Value = D(r.ServiceYear);
            ws.Cell(rn, 8).Value = D(r.ServiceMonth);
            ws.Cell(rn, 9).Value = Math.Round(r.NetAmount, 2);
            ws.Cell(rn, 9).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(rn, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(rn, 10).Value = D(r.SubmissionDate);
            ws.Cell(rn, 11).Value = D(r.TransactionDate);
            ws.Cell(rn, 12).Value = D(r.FileName);
            if (i % 2 == 1) ws.Range(rn, 1, rn, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
        }
        if (list.Count == 0)
        {
            var mr = hr + 1;
            ws.Range(mr, 1, mr, headers.Length).Merge();
            ws.Cell(mr, 1).Value = "No data available for the selected filters.";
            ws.Cell(mr, 1).Style.Font.Italic = true;
            ws.Cell(mr, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(mr, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF0C7");
        }
        else
        {
            var tr = hr + list.Count + 1;
            ws.Range(tr, 1, tr, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#0A2540");
            ws.Range(tr, 1, tr, headers.Length).Style.Font.FontColor = XLColor.White;
            ws.Cell(tr, 1).Value = "TOTALS";
            ws.Cell(tr, 1).Style.Font.Bold = true;
            ws.Cell(tr, 8).Value = $"{list.Count:N0} rows";
            ws.Cell(tr, 8).Style.Font.Bold = true;
            ws.Cell(tr, 9).Value = list.Sum(r => Math.Round(r.NetAmount, 2));
            ws.Cell(tr, 9).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(tr, 9).Style.Font.Bold = true;
            ws.Cell(tr, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        FinishReportSheet(ws, hr, headers.Length, list.Count);
        wb.SaveAs(filePath);
    }

    // ── Live Submission report — downloaded submission/claim FILE metadata
    //    straight from PortalTransactions (no claim matching). File-level.
    //    Backed by IX_PortalTransactions_Submission_Cover so it runs index-only. ──
    private async Task GenerateSubmissionXmlWorkbookAsync(ReportRequest report, string filePath,
        List<int> facilityIds, Action<string, int, int, int, string?> updateStage)
    {
        updateStage("Querying submission files", 20, 0, 0, "Query: PortalTransactions downloaded files.");
        var q = _context.PortalTransactions.AsNoTracking().Where(t => t.FileDownloaded);
        if (facilityIds.Count > 0) q = q.Where(t => facilityIds.Contains(t.FacilityId));
        // PERF: PortalTransactions stores multi-MB FileContentXml/RawXml blobs
        // BEFORE the metadata columns we need, so reading any post-blob column
        // walks the blob overflow chain per row. ORDER BY SyncedAt (the LAST
        // column) forced that traversal across the whole set and hung the report.
        //   • Order by Id (rowid) — a chronological proxy that is free to sort.
        //   • A covering index (IX_PortalTransactions_Submission_Cover) lets this
        //     run index-only, avoiding the blobs entirely.
        //   • CommandTimeout guarantees the job fails fast instead of hanging
        //     forever if the index is not yet present.
        _context.Database.SetCommandTimeout(180);
        var list = await q.OrderByDescending(t => t.Id).Take(50000)
            .Select(t => new
            {
                t.FacilityId, t.Portal, t.TransactionId, t.Type, t.Direction,
                t.FileId, t.FileName, t.FileDownloadedAt, t.FileSizeBytes,
                t.TransactionDate, t.Payer, t.Amount, t.Operation, t.SyncPeriod, t.SyncedAt
            })
            .ToListAsync();

        var rows = list.Where(t =>
        {
            var d = ParseDhpoDate(t.TransactionDate);
            return !d.HasValue || (d.Value.Date >= report.DateFrom.Date && d.Value.Date <= report.DateTo.Date);
        }).ToList();

        var facNames = await _context.Facilities.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name);

        updateStage("Generating workbook", 80, rows.Count, rows.Count, $"Writing {rows.Count:N0} file row(s).");
        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Inter";
        var ws = wb.Worksheets.Add("Submission XML Files");
        const int hr = 8;
        var headers = new[]
        {
            "Facility", "Portal", "Transaction ID", "Type", "Direction", "File ID", "File Name",
            "Downloaded", "Size (KB)", "Transaction Date", "Payer", "Amount", "Operation", "Sync Period", "Synced At"
        };
        ApplyGhafReportHeader(ws, headers.Length, report, rows.Count, 0);
        WriteReportHeaderRow(ws, headers, hr);
        for (int i = 0; i < rows.Count; i++)
        {
            var t = rows[i]; var rn = hr + 1 + i;
            ws.Cell(rn, 1).Value = facNames.TryGetValue(t.FacilityId, out var fn) ? fn : $"Facility {t.FacilityId}";
            ws.Cell(rn, 2).Value = t.Portal;
            ws.Cell(rn, 3).Value = t.TransactionId;
            ws.Cell(rn, 4).Value = t.Type;
            ws.Cell(rn, 5).Value = t.Direction ?? "";
            ws.Cell(rn, 6).Value = t.FileId ?? "";
            ws.Cell(rn, 7).Value = t.FileName ?? "";
            ws.Cell(rn, 8).Value = t.FileDownloadedAt?.ToString("dd/MM/yyyy HH:mm") ?? "Yes";
            ws.Cell(rn, 9).Value = t.FileSizeBytes.HasValue ? Math.Round(t.FileSizeBytes.Value / 1024.0, 1) : 0;
            ws.Cell(rn, 10).Value = t.TransactionDate ?? "";
            ws.Cell(rn, 11).Value = t.Payer ?? "";
            ws.Cell(rn, 12).Value = t.Amount ?? "";
            ws.Cell(rn, 13).Value = t.Operation;
            ws.Cell(rn, 14).Value = t.SyncPeriod ?? "";
            ws.Cell(rn, 15).Value = t.SyncedAt.ToString("dd/MM/yyyy HH:mm");
            if (i % 2 == 1) ws.Row(rn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7FCFA");
        }
        FinishReportSheet(ws, hr, headers.Length, rows.Count);
        wb.SaveAs(filePath);
    }

    private static bool HasDenialCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        var s = json.Trim();
        return s.Length > 2 && s != "[]" && !s.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDenialCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (arr != null && arr.Count > 0)
                return string.Join(", ", arr.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch { }
        return json.Trim('[', ']', '"', ' ');
    }

    // Shared executive report palette (matches GenerateRemittanceWorkbookAsync).
    private const string RptNavy = "#0A2540", RptMuted = "#64748B", RptInk = "#0F172A", RptLine = "#E2E8F0";

    private void ApplyGhafReportHeader(IXLWorksheet ws, int lastColumn, ReportRequest report, int rowCount, int unmatchedRemittanceCount)
    {
        var title = GetReportTitle(report.ReportType).ToUpperInvariant();
        var generatedLocal = DateTime.Now;
        var period = $"{report.DateFrom:dd MMM yyyy} – {report.DateTo:dd MMM yyyy}";
        var facility = report.Branch?.Name ?? "All Facilities";

        // Executive header: navy title band + structured summary block.
        // Unbranded — no logo, company/application name, taglines, or contacts.
        // Table header lands at row 8 (unchanged) so all callers stay aligned.
        ws.Range(1, 1, 3, lastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml(RptNavy);
        ws.Range(1, 1, 3, lastColumn).Merge();
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 20;
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(1, 1).Style.Alignment.Indent = 1;

        AddReportMeta(ws, 5, 1, "Facility", facility);
        AddReportMeta(ws, 5, 4, "Reporting Period", period);
        AddReportMeta(ws, 5, 8, "Rows", rowCount.ToString("N0", CultureInfo.InvariantCulture));
        AddReportMeta(ws, 5, 11, "Generated", generatedLocal.ToString("dd MMM yyyy HH:mm"));
        AddReportMeta(ws, 5, 14, "Report ID", report.ReportId);
        if (unmatchedRemittanceCount > 0)
            AddReportMeta(ws, 5, 17, "Ledger Notes", unmatchedRemittanceCount.ToString("N0", CultureInfo.InvariantCulture));

        ws.Range(7, 1, 7, lastColumn).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        ws.Range(7, 1, 7, lastColumn).Style.Border.BottomBorderColor = XLColor.FromHtml(RptLine);

        ws.Row(1).Height = 30;
        ws.Row(2).Height = 4;
        ws.Row(4).Height = 6;
        ws.Row(5).Height = 14;
        ws.Row(6).Height = 20;
        ws.Row(7).Height = 6;
    }

    // Stacked summary field: small-caps muted label (row) over bold value (row+1).
    private void AddReportMeta(IXLWorksheet ws, int row, int column, string label, string value)
    {
        ws.Cell(row, column).Value = label.ToUpperInvariant();
        ws.Cell(row, column).Style.Font.FontSize = 8;
        ws.Cell(row, column).Style.Font.Bold = true;
        ws.Cell(row, column).Style.Font.FontColor = XLColor.FromHtml(RptMuted);

        ws.Range(row + 1, column, row + 1, column + 2).Merge();
        ws.Cell(row + 1, column).Value = value;
        ws.Cell(row + 1, column).Style.Font.FontSize = 11;
        ws.Cell(row + 1, column).Style.Font.Bold = true;
        ws.Cell(row + 1, column).Style.Font.FontColor = XLColor.FromHtml(RptInk);
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
        ws.Columns(33, 41).Style.Alignment.WrapText = true;
        ws.Column(2).Style.Font.FontColor = XLColor.FromHtml(GhafPrimary);
        ws.Column(2).Style.Font.Bold = true;
        ws.Column(16).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(26).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.Top = 0.4;
        ws.PageSetup.Margins.Bottom = 0.5;
        ws.PageSetup.Margins.Left = 0.25;
        ws.PageSetup.Margins.Right = 0.25;
        ws.SheetView.ZoomScale = 90;
        // Repeat the title band + summary + column headers on every printed page,
        // and a clean unbranded footer (page numbers only).
        ws.PageSetup.SetRowsToRepeatAtTop(1, 8);
        ws.PageSetup.Footer.Right.AddText("Page ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(" of ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);

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
        ws.Column(29).Width = 30;
        ws.Column(33).Width = 36;
        ws.Column(34).Width = 36;
        ws.Column(37).Width = 20;
        ws.Column(38).Width = 32;
        ws.Column(41).Width = 20;
    }

    private string? ResolveReportLogoPath()
    {
        var candidates = new[]
        {
            Path.Combine(_env.WebRootPath, "images", "ghaf-logo-primary-006884-2x.jpg"),
            Path.Combine(_env.WebRootPath, "images", "ghaf-logo-primary-006884.jpg"),
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
        "SubmissionXML" => "Submission XML File Report",
        "LiveSubmission" => "Live Submission Report",
        // Unbranded fallback — the title must never carry company/app branding.
        _ => "Analytics Report"
    };

    private static string GetWorksheetName(string reportType)
    {
        var title = reportType switch
        {
            "ClaimSummary" => "Claim Summary",
            "ClaimActivity" => "Claim Activity",
            "RemittanceActivity" => "Remittance Activity",
            "ClaimReceiver" => "Claim Receiver",
            "ClaimClinician" => "Claim Clinician",
            "FinanceTAT" => "Finance TAT",
            "DenialReport" => "Denial Query",
            "ClaimLifeCycle" => "Claim Life Cycle",
            "SubmissionXML" => "Submissions",
            "LiveSubmission" => "Live Submissions",
            _ => "Report"
        };

        return title.Length <= 31 ? title : title[..31];
    }

    // ── XML parsers ────────────────────────────────────────────────────

    private static ClaimRow MapParsedSubmission(
        XmlParsedRecord record,
        string facilityName,
        IReadOnlyDictionary<string, string> payerLookup)
    {
        var receiverId = record.ReceiverId ?? "";
        var payerId = record.PayerId ?? "";

        var diagDisplay = "";
        if (!string.IsNullOrWhiteSpace(record.DiagnosesJson))
        {
            try
            {
                var diags = JsonSerializer.Deserialize<List<DiagnosisEntry>>(record.DiagnosesJson);
                if (diags?.Count > 0)
                    diagDisplay = string.Join(", ", diags.Select(d =>
                        string.IsNullOrWhiteSpace(d.Type) ? d.Code : $"{d.Code} ({d.Type})"));
            }
            catch { /* ignore malformed JSON */ }
        }

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
            GrossAmtInitial = record.GrossAmount,
            NetAmtInitial = record.NetAmount,
            IdPayer = record.IdPayer ?? "",
            SubmissionFile = record.FileName ?? record.FileId ?? "",
            ResubmissionType = record.ResubmissionType ?? "",
            PrincipalDiagnosis = record.PrincipalDiagnosis ?? "",
            DiagnosesDisplay = diagDisplay,
            PatientGender = record.PatientGender ?? "",
            PatientDob = record.PatientDob ?? "",
            PatientNationalId = record.PatientNationalId ?? ""
        };
    }

    private class DiagnosisEntry
    {
        public string Type { get; set; } = "";
        public string Code { get; set; } = "";
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
            var principalDiag = string.Join(" | ",
                                     claim.Elements("Diagnosis")
                                          .Select(d => d.Element("Code")?.Value)
                                          .Where(c => !string.IsNullOrWhiteSpace(c))
                                          .Distinct(StringComparer.OrdinalIgnoreCase));
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
            if (type.Equals("reconciliation", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("reconciled", StringComparison.OrdinalIgnoreCase))
                return "Resubmitted - Reconciliation";
            if (type.Equals("recon pending", StringComparison.OrdinalIgnoreCase))
                return "Recon Pending";
            return $"Resubmitted - {type}";
        }

        // No explicit type — infer from submission/RA count pattern
        if (outboundCount > 2)
        {
            if (inboundCount == 0) return "Resubmitted - awaiting RA";
            return "Recon Pending";
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

                    var categories = g.Select(x => x.ClaimCategory ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s) && s != "None")
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
                        Status = paid <= 0 ? "Rejected" : "Paid",
                        ClaimCategory = categories.Count switch
                        {
                            0 => denialCodes.Count > 0 ? "Technical" : "",
                            1 => categories[0],
                            _ => "Mixed"
                        }
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
        public decimal GrossAmtInitial { get; set; }
        public decimal NetAmtInitial { get; set; }
        public decimal NetAmtResubmission { get; set; }
        public string IdPayer { get; set; } = "";
        public string SubmissionFile { get; set; } = "";
        public string ResubmissionFile { get; set; } = "";
        public string RaFile { get; set; } = "";
        public string RaDate { get; set; } = "";
        public string ResubmissionType { get; set; } = "";
        public string PrincipalDiagnosis { get; set; } = "";
        public string DiagnosesDisplay { get; set; } = "";
        public string PatientGender { get; set; } = "";
        public string PatientDob { get; set; } = "";
        public string PatientNationalId { get; set; } = "";
        public string ClaimCategory { get; set; } = "";
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
        public string? ClaimCategory { get; set; }
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
        public string ClaimCategory { get; set; } = "";
    }
}
