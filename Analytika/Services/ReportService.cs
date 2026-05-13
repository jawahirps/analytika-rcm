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
    private readonly AppDbContext _context;
    private readonly ILogger<ReportService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IEmailService _emailService;
    private readonly RemittanceParserService _remittanceParser;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public ReportService(
        AppDbContext context,
        ILogger<ReportService> logger,
        IWebHostEnvironment env,
        IEmailService emailService,
        RemittanceParserService remittanceParser,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _env = env;
        _emailService = emailService;
        _remittanceParser = remittanceParser;
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

            UpdateStage("Parsing inbound remittances", 5, 0, 0, "Parsing downloaded remittance XML before matching claims.");
            var (parsed, skipped, errors) = await _remittanceParser.ParsePendingAsync(report.BranchId);
            UpdateStage("Parsing inbound remittances", 15, parsed, parsed + skipped + errors, $"Parsed {parsed} remittance file(s), skipped {skipped}, errors {errors}.");

            var payerLookup = await LoadPayerLookupAsync();

            // ── Parse outbound claim submissions ───────────────────────
            UpdateStage("Parsing outbound claims", 25, 0, 0, "Loading claim submission XML before matching.");
            var claimQuery = _context.PortalTransactions
                .AsNoTracking()
                .Where(t => t.Portal == "DHA"
                         && t.FileContentXml != null && t.FileContentXml.Length > 10
                         && t.Type == "Claim");

            if (report.BranchId.HasValue)
                claimQuery = claimQuery.Where(t => t.FacilityId == report.BranchId);

            var claimTxs = await claimQuery
                .Select(t => new { t.FileId, t.FileName, t.FacilityId, t.FileContentXml, t.TransactionDate })
                .Take(20000)
                .ToListAsync();
            UpdateStage("Parsing outbound claims", 35, claimTxs.Count, claimTxs.Count, $"Loaded {claimTxs.Count:N0} claim submission file(s).");

            // ── Load parsed remittance claims and build a claim-id lookup ──
            var remittanceClaimsQuery = _context.RemittanceClaims
                .AsNoTracking()
                .Include(rc => rc.RemittanceTransaction)
                .Where(rc => rc.RemittanceTransaction != null
                          && rc.RemittanceTransaction.Portal == "DHA"
                          && rc.RemittanceTransaction.FileContentXml != null
                          && rc.RemittanceTransaction.FileContentXml.Length > 10);

            if (report.BranchId.HasValue)
                remittanceClaimsQuery = remittanceClaimsQuery.Where(rc => rc.FacilityId == report.BranchId);

            var remittanceClaims = await remittanceClaimsQuery
                .Select(rc => new RemittanceClaimRow
                {
                    ClaimId = rc.ClaimId,
                    PaidAmount = rc.PaidAmount,
                    OriginalAmount = rc.OriginalAmount,
                    SettlementDate = rc.SettlementDate,
                    PaymentReference = rc.PaymentReference,
                    DenialCodesJson = rc.DenialCodesJson,
                    Comments = rc.Comments,
                    FileName = rc.RemittanceTransaction!.FileName,
                    TransactionDate = rc.RemittanceTransaction.TransactionDate
                })
                .ToListAsync();

            var raLookup = AggregateRemittances(remittanceClaims);
            UpdateStage("Matching inbound and outbound", 55, raLookup.Count, remittanceClaims.Count, $"Matched {raLookup.Count:N0} remittance claim(s) by Claim ID.");

            // ── Facility name lookup ───────────────────────────────────
            var facilityNames = await _context.Facilities.AsNoTracking()
                .ToDictionaryAsync(f => f.Id, f => f.Name);

            // ── Build rows only after both sides are parsed and matched ──
            var rows = new List<ClaimRow>();
            var outboundCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var outboundFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
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

            for (int i = 0; i < claimTxs.Count; i++)
            {
                var tx = claimTxs[i];
                var facilityName = facilityNames.TryGetValue(tx.FacilityId, out var fn) ? fn : $"Facility {tx.FacilityId}";
                foreach (var row in ParseClaimXml(tx.FileContentXml!, tx.FileId, tx.FileName, tx.TransactionDate, facilityName, payerLookup))
                {
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

                    if (!string.IsNullOrWhiteSpace(row.ClaimId))
                    {
                        outboundCounts[row.ClaimId] = outboundCounts.TryGetValue(row.ClaimId, out var count) ? count + 1 : 1;
                        AddToLookup(outboundFiles, row.ClaimId, row.SubmissionFile);
                        AddToLookup(outboundResubTypes, row.ClaimId, row.ResubmissionType);
                    }

                    raLookup.TryGetValue(row.ClaimId, out var ra);
                    row.Ra = ra;

                    var outboundCount = !string.IsNullOrWhiteSpace(row.ClaimId) && outboundCounts.TryGetValue(row.ClaimId, out var obCount) ? obCount : 1;
                    var inboundCount = !string.IsNullOrWhiteSpace(row.ClaimId) && inboundCounts.TryGetValue(row.ClaimId, out var ibCount) ? ibCount : 0;
                    var resubTypes = !string.IsNullOrWhiteSpace(row.ClaimId) && outboundResubTypes.TryGetValue(row.ClaimId, out var types) ? types : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    row.OutboundCount = outboundCount;
                    row.InboundCount = inboundCount;
                    row.RecordCount = outboundCount + inboundCount;
                    row.SubmissionLevel = DetermineSubmissionLevel(outboundCount, inboundCount, resubTypes);

                    if (!string.IsNullOrWhiteSpace(row.ClaimId) && outboundFiles.TryGetValue(row.ClaimId, out var files))
                        row.SubmissionFile = string.Join(" | ", files.Where(v => !string.IsNullOrWhiteSpace(v)));
                    if (ra != null)
                    {
                        row.RaFile = ra.RaFile;
                        row.RaDate = ra.RaDate;
                    }
                    rows.Add(row);
                }

                if (i == 0 || (i + 1) % 100 == 0 || i + 1 == claimTxs.Count)
                {
                    var pct = 50 + (int)Math.Round(((i + 1) / (double)Math.Max(1, claimTxs.Count)) * 35);
                    UpdateStage("Matching inbound and outbound", Math.Min(85, pct), i + 1, claimTxs.Count, $"Matched {i + 1:N0} of {claimTxs.Count:N0} claim file(s).");
                }
            }

            var exportRows = rows
                .GroupBy(r => r.ClaimId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r =>
                {
                    var dt = ParseDhpoDate(r.SubmissionDate) ?? ParseDhpoDate(r.TreatmentDate);
                    return dt ?? DateTime.MinValue;
                }).First())
                .ToList();

            // ── Build Excel ────────────────────────────────────────────
            UpdateStage("Generating workbook", 90, exportRows.Count, exportRows.Count, $"Writing {exportRows.Count:N0} matched row(s) to Excel.");
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Claim Summary");

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
            for (int i = 0; i < exportRows.Count; i++)
            {
                var r = exportRows[i];
                var ra = r.Ra;
                var rn = i + 2;

                var netInitial = r.NetAmtInitial;
                var approvedAmt = ra?.ApprovedAmt ?? 0m;
                var receivedAmt = ra?.ReceivedAmt ?? 0m;
                var unsettled = ra == null ? netInitial : Math.Max(0m, netInitial - approvedAmt);
                var rejInitial = ra == null ? 0m : Math.Max(0m, netInitial - approvedAmt);
                var payStatus = ra == null ? "Pending" : (approvedAmt <= 0 ? "Rejected" : approvedAmt < netInitial - 0.01m ? "Partial" : "Paid");

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
                ws.Cell(rn, 20).Value = 0m;           // Resubmission net — not yet available
                ws.Cell(rn, 21).Value = approvedAmt;
                ws.Cell(rn, 22).Value = rejInitial;
                ws.Cell(rn, 23).Value = 0m;           // Rejected resubmission
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
                    ws.Row(rn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8F9FA");

                // Amount columns format
                foreach (var col in new[] { 18, 19, 20, 21, 22, 23, 24 })
                    ws.Cell(rn, col).Style.NumberFormat.Format = "#,##0.00";
            }

            ws.Row(1).Height = 20;
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            // Auto-filter
            ws.RangeUsed()?.SetAutoFilter();

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

    // ── XML parsers ────────────────────────────────────────────────────

    private static IEnumerable<ClaimRow> ParseClaimXml(
        string xml, string? fileId, string? fileName, string? txDate, string facilityName,
        IReadOnlyDictionary<string, string> payerLookup)
    {
        if (string.IsNullOrEmpty(xml)) yield break;
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { yield break; }

        var header = doc.Root?.Element("Header");
        var receiverId = header?.Element("ReceiverID")?.Value ?? "";
        var submDate = header?.Element("TransactionDate")?.Value ?? txDate ?? "";

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

        var header = doc.Root?.Element("Header");
        var raDate = header?.Element("TransactionDate")?.Value ?? txDate ?? "";
        var payRef = header?.Element("PaymentReference")?.Value
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
                ClaimId = claimId,
                ApprovedAmt = approved,
                ReceivedAmt = received,
                RaFile = fileName ?? "",
                RaDate = raDate,
                SettlementDate = raDate,
                PaymentRef = payRef,
                DenialCode = denialCode,
                DenialDescription = denialDesc,
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
        public string IdPayer { get; set; } = "";
        public string SubmissionFile { get; set; } = "";
        public string RaFile { get; set; } = "";
        public string RaDate { get; set; } = "";
        public string ResubmissionType { get; set; } = "";
        public string PrincipalDiagnosis { get; set; } = "";
        public RaEntry? Ra { get; set; }
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
