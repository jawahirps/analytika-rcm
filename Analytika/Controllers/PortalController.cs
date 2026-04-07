using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Analytika.Controllers;

[Authorize]
public class PortalController : Controller
{
    private readonly AppDbContext _db;
    private readonly IDhaPortalService _dha;
    private readonly IRhaPortalService _rha;
    private readonly PortalSyncService _sync;
    private readonly ReconciliationService _reconciliation;
    private readonly IMemoryCache _cache;

    public PortalController(AppDbContext db, IDhaPortalService dha, IRhaPortalService rha, PortalSyncService sync, ReconciliationService reconciliation, IMemoryCache cache)
    {
        _db = db;
        _dha = dha;
        _rha = rha;
        _sync = sync;
        _reconciliation = reconciliation;
        _cache = cache;
    }

    [HttpGet]
    public async Task<IActionResult> Fetch()
    {
        var vm = await BuildFetchVmAsync();
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fetch(PortalFetchViewModel vm)
    {
        var freshVm = await BuildFetchVmAsync();
        freshVm.Portal      = vm.Portal;
        freshVm.FacilityIds          = vm.FacilityIds;
        freshVm.Operation            = vm.Operation;
        freshVm.DateFrom             = vm.DateFrom;
        freshVm.DateTo               = vm.DateTo;
        freshVm.SearchText           = vm.SearchText;
        freshVm.TransactionStatuses  = vm.TransactionStatuses.Count > 0 ? vm.TransactionStatuses : new() { 1 };
        freshVm.Directions           = vm.Directions.Count > 0 ? vm.Directions : new() { 2 };
        freshVm.TransactionIds       = vm.TransactionIds.Count > 0 ? vm.TransactionIds : new() { 2 };
        freshVm.MinRecord            = vm.MinRecord;
        freshVm.MaxRecord = vm.MaxRecord;
        freshVm.FileId = vm.FileId;

        if (vm.FacilityId == null)
        {
            freshVm.IsError = true;
            freshVm.StatusMessage = "Please select a facility.";
            return View(freshVm);
        }

        var cred = await _db.PortalCredentials
            .FirstOrDefaultAsync(c => c.Portal == vm.Portal && c.FacilityId == vm.FacilityId && c.IsActive);

        if (cred == null)
        {
            freshVm.IsError = true;
            freshVm.StatusMessage = $"No active {vm.Portal} credentials found for this facility. Please configure credentials in Admin → Credentials.";
            return View(freshVm);
        }

        var pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));
        var fetchedBy = User.Identity?.Name ?? "system";
        var log = new PortalFetchLog
        {
            Portal = vm.Portal,
            FacilityId = vm.FacilityId.Value,
            Operation = vm.Operation,
            FetchedBy = fetchedBy
        };

        try
        {
            if (vm.Portal == "DHA")
            {
                if (vm.Operation == "DownloadTransactionFile")
                {
                    if (string.IsNullOrWhiteSpace(vm.FileId))
                    {
                        freshVm.IsError = true;
                        freshVm.StatusMessage = "File ID is required for DownloadTransactionFile.";
                        log.Status = "Failed"; log.ResponseSummary = freshVm.StatusMessage;
                    }
                    else
                    {
                        var (dlResult, dlFileName, dlBytes, dlError) = await _dha.DownloadTransactionFileAsync(cred.Username, pwd, vm.FileId);
                        if (dlError != null)
                        {
                            freshVm.IsError = true;
                            freshVm.StatusMessage = $"Download failed: {dlError}";
                            log.Status = "Failed"; log.ResponseSummary = dlError;
                        }
                        else if (dlBytes != null && dlBytes.Length > 0)
                        {
                            // Save file and mark downloaded
                            var dir = Path.Combine("wwwroot", "portal-downloads");
                            Directory.CreateDirectory(dir);
                            var safeName = string.IsNullOrWhiteSpace(dlFileName) ? $"tx_{vm.FileId}.xml" : dlFileName;
                            var filePath = Path.Combine(dir, safeName);
                            await System.IO.File.WriteAllBytesAsync(filePath, dlBytes);
                            freshVm.DownloadedFileName = safeName;
                            freshVm.DownloadedFileBase64 = Convert.ToBase64String(dlBytes);
                            freshVm.StatusMessage = $"File '{safeName}' downloaded ({dlBytes.Length:N0} bytes).";
                            log.Status = "Success"; log.RecordsFetched = 1; log.ResponseSummary = freshVm.StatusMessage;
                        }
                        else
                        {
                            freshVm.StatusMessage = $"DownloadTransactionFile returned result={dlResult} but no file content. The file may not exist or has already been downloaded.";
                            log.Status = "Success"; log.ResponseSummary = freshVm.StatusMessage;
                        }
                    }
                }
                else
                {
                    (int count, List<PortalFetchResultRow> rows, string? error) fetchResult;

                    if (vm.Operation == "GetNewTransactions")
                        fetchResult = await _dha.GetNewTransactionsAsync(cred.Username, pwd);
                    else if (vm.Operation == "GetNewPriorAuthorizations")
                        fetchResult = await _dha.GetNewPriorAuthorizationsAsync(cred.Username, pwd);
                    else // SearchTransactions — convert dates to DHPO format dd/MM/yyyy HH:mm:ss
                    {
                        // Auto-split into 90-day chunks (portal rejects > 100 days, error -5)
                        DateTime.TryParse(vm.DateFrom, out var parsedFrom);
                        DateTime.TryParse(vm.DateTo,   out var parsedTo);
                        var fetchChunks  = PortalSyncService.GetDateChunks(parsedFrom, parsedTo, 90);
                        var allChunkRows = new List<PortalFetchResultRow>();
                        string? chunkErr = null; int chunkCount = 0;
                        foreach (var (cs, ce) in fetchChunks)
                        {
                            var dhpoFrom = DhaPortalService.FormatDhpoDate(cs.ToString("yyyy-MM-dd"));
                            var dhpoTo   = DhaPortalService.FormatDhpoDate(ce.ToString("yyyy-MM-dd"), endOfDay: true);
                            var (cnt, chunkRows, err) = await _dha.SearchTransactionsAsync(cred.Username, pwd, vm.Direction, dhpoFrom, dhpoTo, vm.TransactionStatus, vm.TransactionId > 0 ? vm.TransactionId : 2, freshVm.MinRecord, freshVm.MaxRecord);
                            allChunkRows.AddRange(chunkRows);
                            chunkCount += cnt; chunkErr ??= err;
                        }
                        fetchResult = (chunkCount, allChunkRows.GroupBy(r => r.FileId).Select(g => g.First()).ToList(), chunkErr);
                    }

                    freshVm.Results = fetchResult.rows;
                    freshVm.TotalFetched = fetchResult.count > 0 ? fetchResult.count : fetchResult.rows.Count;
                    freshVm.IsError = fetchResult.error != null;
                    freshVm.StatusMessage = fetchResult.error ?? $"Fetched {freshVm.TotalFetched} records successfully.";
                    log.RecordsFetched = freshVm.TotalFetched;
                    log.Status = fetchResult.error == null ? "Success" : "Failed";
                    log.ResponseSummary = freshVm.StatusMessage;
                }
            }
            else // RHA
            {
                var (token, authErr) = await _rha.AuthenticateAsync(cred.Username, pwd, cred.ApiBaseUrl ?? "https://tmbapi.riayati.ae:8083");
                if (authErr != null)
                {
                    freshVm.IsError = true;
                    freshVm.StatusMessage = $"RHA Auth failed: {authErr}";
                    log.Status = "Failed"; log.ResponseSummary = freshVm.StatusMessage;
                }
                else
                {
                    (List<PortalFetchResultRow> rows, string? error) rhaResult;
                    if (vm.Operation == "GetRemittances")
                        rhaResult = await _rha.GetRemittancesAsync(token!, cred.ApiBaseUrl ?? "", vm.DateFrom, vm.DateTo);
                    else if (vm.Operation == "GetPriorAuthorizations")
                        rhaResult = await _rha.GetPriorAuthorizationsAsync(token!, cred.ApiBaseUrl ?? "", vm.DateFrom, vm.DateTo);
                    else
                        rhaResult = await _rha.GetClaimsAsync(token!, cred.ApiBaseUrl ?? "", vm.DateFrom, vm.DateTo);

                    freshVm.Results = rhaResult.rows;
                    freshVm.TotalFetched = rhaResult.rows.Count;
                    freshVm.IsError = rhaResult.error != null;
                    freshVm.StatusMessage = rhaResult.error ?? $"Fetched {freshVm.TotalFetched} records successfully.";
                    log.RecordsFetched = freshVm.TotalFetched;
                    log.Status = rhaResult.error == null ? "Success" : "Failed";
                    log.ResponseSummary = freshVm.StatusMessage;
                }
            }
        }
        catch (Exception ex)
        {
            freshVm.IsError = true;
            freshVm.StatusMessage = $"Error: {ex.Message}";
            log.Status = "Failed"; log.ResponseSummary = ex.Message;
        }

        _db.PortalFetchLogs.Add(log);
        await _db.SaveChangesAsync();

        freshVm.RecentLogs = await _db.PortalFetchLogs
            .Include(l => l.Facility)
            .AsNoTracking()
            .OrderByDescending(l => l.FetchedAt)
            .Take(10)
            .ToListAsync();

        return View(freshVm);
    }

    // ── Sync to DB ─────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Sync()
    {
        var vm = await BuildSyncVmAsync();
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(PortalSyncViewModel vm)
    {
        var freshVm = await BuildSyncVmAsync();
        freshVm.Portal = vm.Portal;
        freshVm.FacilityId = vm.FacilityId;
        freshVm.DateFrom = vm.DateFrom;
        freshVm.DateTo = vm.DateTo;
        freshVm.IncludeSearchTransactions = vm.IncludeSearchTransactions;
        freshVm.IncludeNewTransactions = vm.IncludeNewTransactions;
        freshVm.HasRun = true;

        if (vm.FacilityId == null)
        {
            freshVm.IsError = true;
            freshVm.StatusMessage = "Please select a facility.";
            return View(freshVm);
        }

        var cred = await _db.PortalCredentials
            .FirstOrDefaultAsync(c => c.Portal == vm.Portal && c.FacilityId == vm.FacilityId && c.IsActive);

        if (cred == null)
        {
            freshVm.IsError = true;
            freshVm.StatusMessage = $"No active {vm.Portal} credentials found for this facility.";
            return View(freshVm);
        }

        var pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));

        // Parse date range
        if (!DateTime.TryParse(vm.DateFrom, out var dateFrom))
            dateFrom = DateTime.Today.AddYears(-1);
        if (!DateTime.TryParse(vm.DateTo, out var dateTo))
            dateTo = DateTime.Today;

        // Chunk into monthly batches
        var months = new List<(DateTime Start, DateTime End)>();
        var cursor = new DateTime(dateFrom.Year, dateFrom.Month, 1);
        while (cursor <= dateTo)
        {
            var end = new DateTime(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month));
            months.Add((cursor < dateFrom ? dateFrom : cursor, end > dateTo ? dateTo : end));
            cursor = cursor.AddMonths(1);
        }

        string? rhaToken = null;
        if (vm.Portal == "RHA")
        {
            var (tok, authErr) = await _rha.AuthenticateAsync(cred.Username, pwd, cred.ApiBaseUrl ?? "https://tmbapi.riayati.ae:8083");
            if (authErr != null)
            {
                freshVm.IsError = true;
                freshVm.StatusMessage = $"RHA Auth failed: {authErr}";
                return View(freshVm);
            }
            rhaToken = tok;
        }

        // Process each month
        foreach (var (start, end) in months)
        {
            var fromStr = start.ToString("yyyy-MM-dd");
            var toStr   = end.ToString("yyyy-MM-dd");
            var period  = start.ToString("yyyy-MM");
            var label   = start.ToString("MMM yyyy");

            if (vm.Portal == "DHA")
            {
                // DHA Sync Strategy (per DHPO Spec V3.0):
                // 1. SearchTransactions × combos: direction(1=sent,2=received) × txType(2,8,16,32)
                //    → discovers all File records as <File FileID='' FileName='' .../>
                //    Date format: "dd/MM/yyyy HH:mm:ss" (SPEC REQUIREMENT)
                // 2. DownloadTransactionFile(FileID) → get raw XML file bytes
                // 3. Save to DB  4. Reconcile Remittance vs Claim on ClaimID
                var batch = new SyncBatchResult { Period = period, Label = label, Operation = "Search+Download" };
                try
                {
                    // Convert ISO dates to DHPO format
                    var dhpoFrom = DhaPortalService.FormatDhpoDate(fromStr);
                    var dhpoTo   = DhaPortalService.FormatDhpoDate(toStr, endOfDay: true);

                    // 8 searches: direction(1=sent,2=received) × txType(2=Claim,8=Remittance,16=PAReq,32=PAAuth)
                    // status=1 (new/undownloaded only) — portal rejects transactionID=-1 with error -3
                    var txTypes = new[] { 2, 8, 16, 32 };
                    var allRowsList = new List<PortalFetchResultRow>();
                    string? firstErr = null;
                    foreach (var txType in txTypes)
                    {
                        var (_, rowsSent, eSent) = await _dha.SearchTransactionsAsync(cred.Username, pwd, 1, dhpoFrom, dhpoTo, 1, txType);
                        var (_, rowsRecv, eRecv) = await _dha.SearchTransactionsAsync(cred.Username, pwd, 2, dhpoFrom, dhpoTo, 1, txType);
                        allRowsList.AddRange(rowsSent);
                        allRowsList.AddRange(rowsRecv);
                        firstErr ??= eSent ?? eRecv;
                    }

                    // Deduplicate by FileID
                    var allRows = allRowsList
                        .GroupBy(r => r.FileId)
                        .Select(g => g.First())
                        .ToList();

                    batch.Fetched = allRows.Count;
                    batch.Error   = firstErr;

                    // Download each file and upsert
                    var (n, d, filesDownloaded) = await _sync.UpsertDhaTransactionsWithDownloadAsync(
                        allRows, cred.Username, pwd, vm.FacilityId!.Value, "SearchTransactions", period, "DHA");

                    batch.NewRecords      = n;
                    batch.Duplicates      = d;
                    batch.FilesDownloaded = filesDownloaded;

                    freshVm.TotalNew             += n;
                    freshVm.TotalDuplicates      += d;
                    freshVm.TotalFilesDownloaded += filesDownloaded;
                    if (batch.Error != null) freshVm.TotalErrors++;
                }
                catch (Exception ex) { batch.Error = ex.Message; freshVm.TotalErrors++; }
                freshVm.BatchResults.Add(batch);
            }
            else // RHA
            {
                // Claims
                {
                    var batch = new SyncBatchResult { Period = period, Label = label, Operation = "GetClaims" };
                    try
                    {
                        var (rows, err) = await _rha.GetClaimsAsync(rhaToken!, cred.ApiBaseUrl ?? "", fromStr, toStr);
                        batch.Fetched = rows.Count;
                        batch.Error = err;
                        var (n, d, _) = await _sync.UpsertDhaTransactionsWithDownloadAsync(rows, "", "", vm.FacilityId!.Value, "GetClaims", period, "RHA", skipDownload: true);
                        batch.NewRecords = n; batch.Duplicates = d;
                        freshVm.TotalNew += n; freshVm.TotalDuplicates += d;
                        if (err != null) freshVm.TotalErrors++;
                    }
                    catch (Exception ex) { batch.Error = ex.Message; freshVm.TotalErrors++; }
                    freshVm.BatchResults.Add(batch);
                }
                // Remittances
                {
                    var batch = new SyncBatchResult { Period = period, Label = label, Operation = "GetRemittances" };
                    try
                    {
                        var (rows, err) = await _rha.GetRemittancesAsync(rhaToken!, cred.ApiBaseUrl ?? "", fromStr, toStr);
                        batch.Fetched = rows.Count;
                        batch.Error = err;
                        var (n, d, _) = await _sync.UpsertDhaTransactionsWithDownloadAsync(rows, "", "", vm.FacilityId!.Value, "GetRemittances", period, "RHA", skipDownload: true);
                        batch.NewRecords = n; batch.Duplicates = d;
                        freshVm.TotalNew += n; freshVm.TotalDuplicates += d;
                        if (err != null) freshVm.TotalErrors++;
                    }
                    catch (Exception ex) { batch.Error = ex.Message; freshVm.TotalErrors++; }
                    freshVm.BatchResults.Add(batch);
                }
            }
        }

        freshVm.TotalInDb = await _db.PortalTransactions
            .Where(t => t.Portal == vm.Portal && t.FacilityId == vm.FacilityId)
            .CountAsync();

        freshVm.StatusMessage = freshVm.TotalErrors > 0
            ? $"Sync complete with {freshVm.TotalErrors} error(s). {freshVm.TotalNew} new records saved, {freshVm.TotalFilesDownloaded} files downloaded."
            : $"Sync complete. {freshVm.TotalNew} new records saved, {freshVm.TotalFilesDownloaded} files downloaded, {freshVm.TotalDuplicates} duplicates skipped.";
        freshVm.IsError = freshVm.TotalErrors > 0 && freshVm.TotalNew == 0;

        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Portal = vm.Portal,
            FacilityId = vm.FacilityId!.Value,
            Operation = "FullSync",
            FetchedBy = User.Identity?.Name ?? "system",
            RecordsFetched = freshVm.TotalNew,
            Status = freshVm.IsError ? "Failed" : "Success",
            ResponseSummary = freshVm.StatusMessage
        });
        await _db.SaveChangesAsync();

        return View(freshVm);
    }

    // ── Sync New Transactions (GetNewTransactions incremental) ──────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNew(int facilityId)
    {
        var cred = await _db.PortalCredentials
            .FirstOrDefaultAsync(c => c.Portal == "DHA" && c.FacilityId == facilityId && c.IsActive);

        if (cred == null)
            return Json(new { ok = false, message = "No active DHA credential for this facility." });

        var pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));
        var period = DateTime.UtcNow.ToString("yyyy-MM");

        try
        {
            // GetNewTransactions — returns files not yet acknowledged via SetTransactionDownloaded
            // Response: <Files><File FileID='' FileName='' SenderID='' ReceiverID='' TransactionDate='' RecordCount=''/></Files>
            var (count, rows, error) = await _dha.GetNewTransactionsAsync(cred.Username, pwd);

            if (error != null && rows.Count == 0)
                return Json(new { ok = false, message = $"GetNewTransactions error: {error}" });

            // Download each file and upsert
            var (newRecords, dups, filesDownloaded) = await _sync.UpsertDhaTransactionsWithDownloadAsync(
                rows, cred.Username, pwd, facilityId, "GetNewTransactions", period, "DHA");

            var msg = $"Sync New complete. {newRecords} new records, {filesDownloaded} files downloaded, {dups} duplicates skipped.";

            _db.PortalFetchLogs.Add(new PortalFetchLog
            {
                Portal = "DHA",
                FacilityId = facilityId,
                Operation = "GetNewTransactions",
                FetchedBy = User.Identity?.Name ?? "system",
                RecordsFetched = newRecords,
                Status = "Success",
                ResponseSummary = msg
            });
            await _db.SaveChangesAsync();

            return Json(new { ok = true, message = msg, newRecords, filesDownloaded, dups });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = $"Error: {ex.Message}" });
        }
    }

    // ── Synced Data Browser ─────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> SyncedData(string? portal, List<int>? facilityId, string? dateFrom, string? dateTo, string? search, int page = 1)
    {
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();

        var query = _db.PortalTransactions.Include(t => t.Facility).AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(portal))              query = query.Where(t => t.Portal == portal);
        if (facilityId != null && facilityId.Count > 0) query = query.Where(t => facilityId.Contains(t.FacilityId));
        if (!string.IsNullOrEmpty(dateFrom)) query = query.Where(t => string.Compare(t.TransactionDate, dateFrom) >= 0);
        if (!string.IsNullOrEmpty(dateTo))   query = query.Where(t => string.Compare(t.TransactionDate, dateTo) <= 0);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.TransactionId.Contains(search) || (t.Payer != null && t.Payer.Contains(search)) || t.Status.Contains(search));

        // Combine counts into a single DB round-trip instead of two separate CountAsync calls
        var counts = await query
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), FilesDownloaded = g.Count(t => t.FileDownloaded) })
            .FirstOrDefaultAsync();
        var total           = counts?.Total ?? 0;
        var filesDownloaded = counts?.FilesDownloaded ?? 0;
        const int pageSize = 50;
        var items = await query
            .OrderByDescending(t => t.SyncedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var vm = new SyncedDataViewModel
        {
            Facilities           = facilities.Select(f => new SelectListItem(f.Name, f.Id.ToString())).ToList(),
            FacilityIds          = facilityId ?? new(),
            Portal               = portal,
            DateFrom             = dateFrom,
            DateTo               = dateTo,
            SearchText           = search,
            Transactions         = items,
            TotalCount           = total,
            FilesDownloadedCount = filesDownloaded,
            PendingFilesCount    = total - filesDownloaded,
            Page                 = page,
            PageSize             = pageSize
        };

        return View(vm);
    }

    // ── Fetch & Save All (SearchTransactions → download → DB) ──────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FetchAndSave(PortalFetchViewModel vm)
    {
        var freshVm = await BuildFetchVmAsync();
        freshVm.Portal               = vm.Portal;
        freshVm.FacilityIds          = vm.FacilityIds;
        freshVm.Operation            = "SearchTransactions";
        freshVm.DateFrom             = vm.DateFrom;
        freshVm.DateTo               = vm.DateTo;
        freshVm.Directions           = vm.Directions.Count > 0 ? vm.Directions : new() { 2 };
        freshVm.TransactionStatuses  = vm.TransactionStatuses.Count > 0 ? vm.TransactionStatuses : new() { 1, 2 };
        freshVm.TransactionIds       = vm.TransactionIds.Count > 0 ? vm.TransactionIds : new() { 2, 8 };
        freshVm.MinRecord            = vm.MinRecord;
        freshVm.MaxRecord            = vm.MaxRecord;

        if (vm.FacilityId == null)
        {
            freshVm.IsError = true; freshVm.StatusMessage = "Please select a facility.";
            return View("Fetch", freshVm);
        }

        var cred = await _db.PortalCredentials
            .FirstOrDefaultAsync(c => c.Portal == "DHA" && c.FacilityId == vm.FacilityId && c.IsActive);

        if (cred == null)
        {
            freshVm.IsError = true;
            freshVm.StatusMessage = "No active DHA credentials found for this facility.";
            return View("Fetch", freshVm);
        }

        if (!DateTime.TryParse(vm.DateFrom, out var parsedFrom) || !DateTime.TryParse(vm.DateTo, out var parsedTo))
        {
            freshVm.IsError = true;
            freshVm.StatusMessage = "Please enter valid dates.";
            return View("Fetch", freshVm);
        }

        var pwd    = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));
        var period = parsedFrom.ToString("yyyy-MM");

        // Auto-split into 90-day chunks (portal rejects > 100 days, error -5)
        var saveChunks  = PortalSyncService.GetDateChunks(parsedFrom, parsedTo, 90);
        var allSaveRows = new List<PortalFetchResultRow>();
        string? searchErr = null;
        foreach (var (cs, ce) in saveChunks)
        {
            var dhpoFrom = DhaPortalService.FormatDhpoDate(cs.ToString("yyyy-MM-dd"));
            var dhpoTo   = DhaPortalService.FormatDhpoDate(ce.ToString("yyyy-MM-dd"), endOfDay: true);
            var (_, chunkRows, err) = await _dha.SearchTransactionsAsync(
                cred.Username, pwd, vm.Direction, dhpoFrom, dhpoTo,
                vm.TransactionStatus, vm.TransactionId > 0 ? vm.TransactionId : 2,
                freshVm.MinRecord, freshVm.MaxRecord);
            allSaveRows.AddRange(chunkRows);
            searchErr ??= err;
        }
        var rows = allSaveRows.GroupBy(r => r.FileId).Select(g => g.First()).ToList();

        if (searchErr != null)
        {
            freshVm.IsError = true; freshVm.StatusMessage = $"Search failed: {searchErr}";
            return View("Fetch", freshVm);
        }

        freshVm.Results      = rows;
        freshVm.TotalFetched = rows.Count;

        if (!rows.Any())
        {
            freshVm.StatusMessage = "Search returned 0 files — nothing to download.";
            return View("Fetch", freshVm);
        }

        var (newCount, dups, filesDownloaded) = await _sync.UpsertDhaTransactionsWithDownloadAsync(
            rows, cred.Username, pwd, vm.FacilityId!.Value, "SearchTransactions", period, "DHA");

        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Portal          = "DHA",
            FacilityId      = vm.FacilityId.Value,
            Operation       = "SearchTransactions+Download",
            FetchedBy       = User.Identity?.Name ?? "system",
            RecordsFetched  = newCount,
            Status          = "Success",
            ResponseSummary = $"{newCount} saved, {filesDownloaded} downloaded, {dups} duplicates"
        });
        await _db.SaveChangesAsync();

        freshVm.RecentLogs    = await _db.PortalFetchLogs.Include(l => l.Facility)
                                    .AsNoTracking().OrderByDescending(l => l.FetchedAt).Take(10).ToListAsync();
        freshVm.StatusMessage = $"Saved {newCount} new record(s) to DB, {filesDownloaded} file(s) downloaded, {dups} duplicate(s) skipped.";
        return View("Fetch", freshVm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FetchAndSaveAjax([FromForm] PortalFetchViewModel vm)
    {
        if (vm.FacilityId == null)
            return Json(new { error = "Please select a facility." });

        var cred = await _db.PortalCredentials
            .FirstOrDefaultAsync(c => c.Portal == "DHA" && c.FacilityId == vm.FacilityId && c.IsActive);
        if (cred == null)
            return Json(new { error = "No active DHA credentials found for this facility." });

        if (!DateTime.TryParse(vm.DateFrom, out var parsedFrom) || !DateTime.TryParse(vm.DateTo, out var parsedTo))
            return Json(new { error = "Please enter valid dates." });

        var pwd    = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));
        var period = parsedFrom.ToString("yyyy-MM");

        var chunks      = PortalSyncService.GetDateChunks(parsedFrom, parsedTo, 90);
        var allRows     = new List<PortalFetchResultRow>();
        string? fetchErr = null;
        foreach (var (cs, ce) in chunks)
        {
            var dhpoFrom = DhaPortalService.FormatDhpoDate(cs.ToString("yyyy-MM-dd"));
            var dhpoTo   = DhaPortalService.FormatDhpoDate(ce.ToString("yyyy-MM-dd"), endOfDay: true);
            // Search both status=1 (new) and status=2 (already downloaded) to catch all records
            foreach (var txStatus in new[] { 1, 2 })
            {
                var (_, chunkRows, err) = await _dha.SearchTransactionsAsync(
                    cred.Username, pwd, vm.Direction, dhpoFrom, dhpoTo,
                    txStatus, vm.TransactionId > 0 ? vm.TransactionId : 2,
                    vm.MinRecord, vm.MaxRecord);
                allRows.AddRange(chunkRows);
                fetchErr ??= err;
            }
        }

        if (fetchErr != null)
            return Json(new { error = $"Search failed: {fetchErr}" });

        var rows = allRows.GroupBy(r => r.FileId).Select(g => g.First()).ToList();
        if (!rows.Any())
            return Json(new { error = (string?)null, found = 0, saved = 0, filesDownloaded = 0, duplicates = 0 });

        var (newCount, dups, filesDownloaded) = await _sync.UpsertDhaTransactionsWithDownloadAsync(
            rows, cred.Username, pwd, vm.FacilityId!.Value, "BulkSave", period, "DHA");

        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Portal          = "DHA",
            FacilityId      = vm.FacilityId.Value,
            Operation       = "BulkSave",
            FetchedBy       = User.Identity?.Name ?? "system",
            RecordsFetched  = newCount,
            Status          = "Success",
            ResponseSummary = $"{newCount} saved, {filesDownloaded} downloaded, {dups} duplicates"
        });
        await _db.SaveChangesAsync();

        return Json(new { error = (string?)null, found = rows.Count, saved = newCount, filesDownloaded, duplicates = dups });
    }

    // ── XML Parsing Dashboard (renamed from Reconciliation) ───────────

    [HttpGet]
    public async Task<IActionResult> XmlParsing(List<int>? facilityId)
    {
        var vm = await _reconciliation.GetXmlParsingStatsAsync(facilityId);
        return View(vm);
    }

    // Keep old URL working
    [HttpGet]
    public IActionResult Reconciliation() => RedirectToAction(nameof(XmlParsing));

    // ── Bulk Save — SSE streaming progress ─────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task FetchAndSaveStream([FromForm] PortalFetchViewModel vm)
    {
        var ct = HttpContext.RequestAborted;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task Send(object obj)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(obj)}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            catch { }
        }

        if (vm.FacilityId == null)
        { await Send(new { status = "error", message = "Please select a facility." }); return; }

        var cred = await _db.PortalCredentials
            .FirstOrDefaultAsync(c => c.Portal == "DHA" && c.FacilityId == vm.FacilityId && c.IsActive, ct);
        if (cred == null)
        { await Send(new { status = "error", message = "No active DHA credentials." }); return; }

        if (!DateTime.TryParse(vm.DateFrom, out var parsedFrom) || !DateTime.TryParse(vm.DateTo, out var parsedTo))
        { await Send(new { status = "error", message = "Invalid dates." }); return; }

        var pwd    = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));
        var period = parsedFrom.ToString("yyyy-MM");

        await Send(new { status = "searching", total = 0, found = 0, saved = 0, downloaded = 0, duplicates = 0, pending = 0 });

        // Comprehensive search: all 4 txTypes × both directions × both statuses (1=new, 2=already downloaded)
        var txTypes    = new[] { 2, 8, 16, 32 };
        var txStatuses = new[] { 1, 2 };
        var chunks     = PortalSyncService.GetDateChunks(parsedFrom, parsedTo, 90);
        var allRows    = new List<PortalFetchResultRow>();

        foreach (var (cs, ce) in chunks)
        {
            if (ct.IsCancellationRequested) return;
            var dhpoFrom = DhaPortalService.FormatDhpoDate(cs.ToString("yyyy-MM-dd"));
            var dhpoTo   = DhaPortalService.FormatDhpoDate(ce.ToString("yyyy-MM-dd"), endOfDay: true);
            foreach (var txStatus in txStatuses)
            foreach (var txType in txTypes)
            {
                var (_, rSent, _) = await _dha.SearchTransactionsAsync(cred.Username, pwd, 1, dhpoFrom, dhpoTo, txStatus, txType);
                var (_, rRecv, _) = await _dha.SearchTransactionsAsync(cred.Username, pwd, 2, dhpoFrom, dhpoTo, txStatus, txType);
                allRows.AddRange(rSent);
                allRows.AddRange(rRecv);
            }
        }

        var rows  = allRows.GroupBy(r => r.FileId).Select(g => g.First()).ToList();
        var total = rows.Count;

        await Send(new { status = "found", total, found = total, saved = 0, downloaded = 0, duplicates = 0, pending = total });

        if (!rows.Any())
        {
            await Send(new { status = "done", total = 0, found = 0, saved = 0, downloaded = 0, duplicates = 0, pending = 0 });
            return;
        }

        int lastSaved = 0, lastDups = 0, lastFiles = 0;

        await _sync.UpsertDhaTransactionsWithDownloadAsync(
            rows, cred.Username, pwd, vm.FacilityId!.Value, "BulkSave", period, "DHA",
            onProgress: async (saved, dups, files) =>
            {
                lastSaved = saved; lastDups = dups; lastFiles = files;
                await Send(new
                {
                    status     = "processing",
                    total,
                    found      = total,
                    saved,
                    downloaded = files,
                    duplicates = dups,
                    pending    = total - saved - dups
                });
            });

        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Portal          = "DHA",
            FacilityId      = vm.FacilityId!.Value,
            Operation       = "BulkSave",
            FetchedBy       = User.Identity?.Name ?? "system",
            RecordsFetched  = lastSaved,
            Status          = "Success",
            ResponseSummary = $"{lastSaved} saved, {lastFiles} downloaded, {lastDups} duplicates"
        });
        await _db.SaveChangesAsync(ct);

        await Send(new
        {
            status     = "done",
            total,
            found      = total,
            saved      = lastSaved,
            downloaded = lastFiles,
            duplicates = lastDups,
            pending    = 0
        });
    }

    // ── Download saved XML file ─────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> DownloadFile(int id)
    {
        var tx = await _db.PortalTransactions.FindAsync(id);
        if (tx?.FileContentXml == null) return NotFound();
        var fileName = (!string.IsNullOrWhiteSpace(tx.FileName) ? tx.FileName : $"{tx.TransactionId}.xml")
            .Replace('/', '_').Replace('\\', '_');
        return File(Encoding.UTF8.GetBytes(tx.FileContentXml), "application/xml", fileName);
    }

    // ── Manual Fetch (AJAX, JSON result) ───────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FetchAjax(string portal, int? facilityId, string? operation,
        string? dateFrom, string? dateTo, string? fileId)
    {
        if (facilityId == null) return Json(new { ok = false, message = "Select a facility." });
        var cred = await _db.PortalCredentials
            .FirstOrDefaultAsync(c => c.Portal == portal && c.FacilityId == facilityId && c.IsActive);
        if (cred == null) return Json(new { ok = false, message = $"No active {portal} credentials for this facility." });

        var pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));
        operation ??= "SearchTransactions";

        try
        {
            if (portal == "DHA")
            {
                if (operation == "DownloadTransactionFile")
                {
                    if (string.IsNullOrWhiteSpace(fileId))
                        return Json(new { ok = false, message = "File ID required." });
                    var (_, dlFileName, dlBytes, dlErr) = await _dha.DownloadTransactionFileAsync(cred.Username, pwd, fileId);
                    if (dlErr != null) return Json(new { ok = false, message = dlErr });
                    return Json(new { ok = true, message = $"Downloaded: {dlFileName} ({dlBytes?.Length ?? 0:N0} bytes)", rows = Array.Empty<object>() });
                }

                (int count, List<PortalFetchResultRow> rows, string? error) result;
                if (operation == "GetNewTransactions")
                    result = await _dha.GetNewTransactionsAsync(cred.Username, pwd);
                else if (operation == "GetNewPriorAuthorizations")
                    result = await _dha.GetNewPriorAuthorizationsAsync(cred.Username, pwd);
                else
                {
                    DateTime.TryParse(dateFrom, out var pFrom);
                    DateTime.TryParse(dateTo, out var pTo);
                    var chunks = PortalSyncService.GetDateChunks(pFrom == default ? DateTime.Today.AddMonths(-1) : pFrom,
                        pTo == default ? DateTime.Today : pTo, 90);
                    var allRows = new List<PortalFetchResultRow>();
                    foreach (var (cs, ce) in chunks)
                    {
                        var df = DhaPortalService.FormatDhpoDate(cs.ToString("yyyy-MM-dd"));
                        var dt = DhaPortalService.FormatDhpoDate(ce.ToString("yyyy-MM-dd"), endOfDay: true);
                        var (_, r, _) = await _dha.SearchTransactionsAsync(cred.Username, pwd, 2, df, dt, 1, 2);
                        allRows.AddRange(r);
                    }
                    result = (allRows.Count, allRows.GroupBy(r => r.FileId).Select(g => g.First()).ToList(), null);
                }
                if (result.error != null) return Json(new { ok = false, message = result.error });
                return Json(new
                {
                    ok = true,
                    message = $"Fetched {result.rows.Count} records.",
                    rows = result.rows.Select(r => new { r.FileId, r.Type, r.Status, r.Payer, r.Amount, r.Date, r.FileName })
                });
            }

            var (token, authErr) = await _rha.AuthenticateAsync(cred.Username, pwd, cred.ApiBaseUrl ?? "");
            if (authErr != null) return Json(new { ok = false, message = authErr });
            (List<PortalFetchResultRow> rows, string? error) rhaRes = operation switch
            {
                "GetRemittances"         => await _rha.GetRemittancesAsync(token!, cred.ApiBaseUrl ?? "", dateFrom, dateTo),
                "GetPriorAuthorizations" => await _rha.GetPriorAuthorizationsAsync(token!, cred.ApiBaseUrl ?? "", dateFrom, dateTo),
                _                        => await _rha.GetClaimsAsync(token!, cred.ApiBaseUrl ?? "", dateFrom, dateTo)
            };
            if (rhaRes.error != null) return Json(new { ok = false, message = rhaRes.error });
            return Json(new
            {
                ok = true,
                message = $"Fetched {rhaRes.rows.Count} records.",
                rows = rhaRes.rows.Select(r => new { r.FileId, r.Type, r.Status, r.Payer, r.Amount, r.Date, r.FileName })
            });
        }
        catch (Exception ex) { return Json(new { ok = false, message = ex.Message }); }
    }

    // ── Sync Single Facility — month-wise for a date range ──────────

    [HttpGet]
    public async Task SyncFacilityStream(int facilityId, string? from = null, string? to = null, int resumeMonthIdx = 0)
    {
        var ct = HttpContext.RequestAborted;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.WriteAsync("retry: 30000\n\n");
        await Response.Body.FlushAsync(ct);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try { while (!heartbeatCts.Token.IsCancellationRequested) { await Task.Delay(20_000, heartbeatCts.Token); await Response.WriteAsync(": ping\n\n", heartbeatCts.Token); await Response.Body.FlushAsync(heartbeatCts.Token); } } catch { }
        }, heartbeatCts.Token);

        async Task Send(object obj)
        {
            if (ct.IsCancellationRequested) return;
            try { await Response.WriteAsync($"data: {JsonSerializer.Serialize(obj)}\n\n", ct); await Response.Body.FlushAsync(ct); } catch { }
        }

        var cred = await _db.PortalCredentials.Include(c => c.Facility)
            .FirstOrDefaultAsync(c => c.FacilityId == facilityId && c.IsActive, ct);
        if (cred == null) { await Send(new { status = "error", message = "Credential not found." }); return; }

        var parsedFrom = from != null ? DateTime.Parse(from) : DateTime.Today.AddYears(-1);
        var parsedTo   = to   != null ? DateTime.Parse(to)   : DateTime.Today;
        int[] txTypes  = [2, 8, 16, 32];
        var facName    = cred.Facility?.Name ?? $"Facility {cred.FacilityId}";

        string pwd;
        try { pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted)); }
        catch { await Send(new { status = "error", message = "Corrupted password." }); return; }

        var monthChunks = BuildMonthChunks(parsedFrom, parsedTo);

        int grandTotal = 0, grandSaved = 0, grandDups = 0, grandFiles = 0;
        int totalSteps = monthChunks.Count;
        int stepsDone  = Math.Min(resumeMonthIdx, totalSteps);  // pre-credit already-done months

        ActiveSyncState.Start(1, monthChunks.Count);
        await Send(new { status = "start", message = $"{facName} · {monthChunks.Count} months ({parsedFrom:yyyy-MM-dd} → {parsedTo:yyyy-MM-dd})", total = 1, months = monthChunks.Count, totalSteps });
        if (resumeMonthIdx > 0)
            await Send(new { status = "resumed", resumeMonthIdx, message = $"Resuming from month {resumeMonthIdx + 1} of {totalSteps}" });
        await Send(new { status = "facility_start", facilityIndex = 1, facilityName = facName });

        for (int mi = 0; mi < monthChunks.Count; mi++)
        {
            if (ct.IsCancellationRequested) break;
            if (mi < resumeMonthIdx) continue;  // skip already-completed months

            var (ms, me, mlabel) = monthChunks[mi];
            var dhpoFrom = DhaPortalService.FormatDhpoDate(ms.ToString("yyyy-MM-dd"));
            var dhpoTo   = DhaPortalService.FormatDhpoDate(me.ToString("yyyy-MM-dd"), endOfDay: true);

            var monthRows   = await _sync.SearchAllCombosAsync(cred.Username, pwd, dhpoFrom, dhpoTo, txTypes);
            var uniqueMonth = PortalSyncService.DeduplicateRows(monthRows);
            grandTotal += uniqueMonth.Count;
            stepsDone++;
            int pct = totalSteps > 0 ? (int)((double)stepsDone / totalSteps * 100) : 0;

            ActiveSyncState.Update(stepsDone, 1, facName, mlabel, grandSaved, grandFiles, pct);
            await Send(new { status = "month_done", facilityIndex = 1, facilityName = facName, month = mlabel, monthIdx = mi, found = uniqueMonth.Count, grandTotal, pct });

            if (uniqueMonth.Any())
            {
                var (ns, nd, nf) = await _sync.UpsertDhaTransactionsWithDownloadAsync(
                    uniqueMonth, cred.Username, pwd, cred.FacilityId, "MonthWiseSync", ms.ToString("yyyy-MM"), "DHA");
                grandSaved += ns; grandDups += nd; grandFiles += nf;
                ActiveSyncState.Update(stepsDone, 1, facName, mlabel, grandSaved, grandFiles, pct);
                await Send(new { status = "processing", facilityIndex = 1, facilityName = facName, month = mlabel, saved = grandSaved, dups = grandDups, files = grandFiles, pct });
            }
        }

        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Portal = "DHA", FacilityId = cred.FacilityId, Operation = "MonthWiseSync",
            FetchedBy = User.Identity?.Name ?? "system",
            RecordsFetched = grandSaved, Status = "Success",
            ResponseSummary = $"{grandSaved} saved, {grandFiles} downloaded, {grandDups} dup — {monthChunks.Count} months"
        });
        await _db.SaveChangesAsync(ct);

        heartbeatCts.Cancel();
        _cache.Remove("statusbar_static");
        ActiveSyncState.Finish(grandSaved, grandFiles);
        await Send(new { status = "facility_done", facilityIndex = 1, facilityName = facName, saved = grandSaved, dups = grandDups, files = grandFiles, pct = 100 });
        await Send(new { status = "done", message = $"Complete — {grandSaved} new · {grandDups} dup · {grandFiles} files · {monthChunks.Count} months", grandTotal, grandSaved, grandDups, grandFiles });
    }

    // ── Sync All Facilities — 2-year bulk download ──────────────────

    [HttpGet]
    public async Task SyncAllFacilitiesStream([FromQuery] List<int>? facilityIds = null, [FromQuery] List<int>? txTypes = null, [FromQuery] int resumeFacilityIdx = 0, [FromQuery] int resumeMonthIdx = 0)
    {
        var ct = HttpContext.RequestAborted;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        // Tell the browser to wait 30 s before attempting to reconnect
        await Response.WriteAsync("retry: 30000\n\n");
        await Response.Body.FlushAsync(ct);

        // Heartbeat — prevents proxy/browser from dropping an idle connection
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(20_000, heartbeatCts.Token);
                    if (heartbeatCts.Token.IsCancellationRequested) break;
                    // SSE comment line — keeps TCP alive, ignored by onmessage
                    await Response.WriteAsync(": ping\n\n", heartbeatCts.Token);
                    await Response.Body.FlushAsync(heartbeatCts.Token);
                }
            }
            catch { }
        }, heartbeatCts.Token);

        async Task Send(object obj)
        {
            if (ct.IsCancellationRequested) return;
            try { await Response.WriteAsync($"data: {JsonSerializer.Serialize(obj)}\n\n", ct); await Response.Body.FlushAsync(ct); }
            catch { }
        }

        var credsQuery = _db.PortalCredentials.Include(c => c.Facility).Where(c => c.IsActive && c.Portal == "DHA");
        if (facilityIds?.Count > 0)
            credsQuery = credsQuery.Where(c => facilityIds.Contains(c.FacilityId));
        var creds = await credsQuery.ToListAsync(ct);

        if (!creds.Any())
        { await Send(new { status = "error", message = "No active DHA credentials found." }); return; }

        var parsedFrom = new DateTime(2024, 1, 1);
        var parsedTo   = DateTime.Today;
        int[] effectiveTxTypes = (txTypes?.Count > 0) ? txTypes.ToArray() : [2, 8, 16, 32];

        var monthChunks = BuildMonthChunks(parsedFrom, parsedTo);

        int grandTotal = 0, grandSaved = 0, grandDups = 0, grandFiles = 0;
        int facilityIndex = 0;
        int totalSteps = creds.Count * monthChunks.Count;
        // Pre-credit already-completed work so progress % starts correctly
        int stepsDone = resumeFacilityIdx * monthChunks.Count + Math.Min(resumeMonthIdx, monthChunks.Count);

        ActiveSyncState.Start(creds.Count, monthChunks.Count);
        await Send(new { status = "start", message = $"Month-wise sync · {creds.Count} facilities · {monthChunks.Count} months ({parsedFrom:yyyy-MM-dd} → {parsedTo:yyyy-MM-dd})", total = creds.Count, months = monthChunks.Count, totalSteps });
        if (resumeFacilityIdx > 0 || resumeMonthIdx > 0)
            await Send(new { status = "resumed", resumeFacilityIdx, resumeMonthIdx, message = $"Resuming from facility {resumeFacilityIdx + 1}, month {resumeMonthIdx + 1}" });

        foreach (var cred in creds)
        {
            if (ct.IsCancellationRequested) break;
            facilityIndex++;
            int fi = facilityIndex - 1;  // 0-based facility index
            var facName = cred.Facility?.Name ?? $"Facility {cred.FacilityId}";

            // Skip fully-completed facilities
            if (fi < resumeFacilityIdx) continue;

            string pwd;
            try { pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted)); }
            catch { await Send(new { status = "warning", message = $"[{facName}] Corrupted password — skipped.", facilityIndex }); stepsDone += monthChunks.Count; continue; }

            await Send(new { status = "facility_start", message = $"[{facilityIndex}/{creds.Count}] {facName}", facilityIndex, facilityFi = fi, facilityName = facName });

            int facSaved = 0, facDups = 0, facFiles = 0;
            int monthStart = (fi == resumeFacilityIdx) ? resumeMonthIdx : 0;

            for (int mi = 0; mi < monthChunks.Count; mi++)
            {
                if (ct.IsCancellationRequested) break;
                if (mi < monthStart) continue;  // skip already-completed months for resume facility

                var (ms, me, mlabel) = monthChunks[mi];
                var dhpoFrom = DhaPortalService.FormatDhpoDate(ms.ToString("yyyy-MM-dd"));
                var dhpoTo   = DhaPortalService.FormatDhpoDate(me.ToString("yyyy-MM-dd"), endOfDay: true);

                // Parallel search — all type/status combos at once (max 4 concurrent)
                var monthRows   = await _sync.SearchAllCombosAsync(cred.Username, pwd, dhpoFrom, dhpoTo, effectiveTxTypes);
                var uniqueMonth = PortalSyncService.DeduplicateRows(monthRows);
                grandTotal += uniqueMonth.Count;
                stepsDone++;
                int pct = totalSteps > 0 ? (int)((double)stepsDone / totalSteps * 100) : 0;

                ActiveSyncState.Update(stepsDone, facilityIndex, facName, mlabel, grandSaved, grandFiles, pct);
                await Send(new { status = "month_done", facilityIndex, facilityFi = fi, facilityName = facName, month = mlabel, monthIdx = mi, found = uniqueMonth.Count, grandTotal, pct });

                if (uniqueMonth.Any())
                {
                    var (ns, nd, nf) = await _sync.UpsertDhaTransactionsWithDownloadAsync(
                        uniqueMonth, cred.Username, pwd, cred.FacilityId, "MonthWiseSync", ms.ToString("yyyy-MM"), "DHA");
                    facSaved += ns; facDups += nd; facFiles += nf;
                    grandSaved += ns; grandDups += nd; grandFiles += nf;

                    ActiveSyncState.Update(stepsDone, facilityIndex, facName, mlabel, grandSaved, grandFiles, pct);
                    await Send(new { status = "processing", facilityIndex, facilityFi = fi, facilityName = facName, month = mlabel, saved = grandSaved, dups = grandDups, files = grandFiles, pct });
                }
            }

            _db.PortalFetchLogs.Add(new PortalFetchLog
            {
                Portal = "DHA", FacilityId = cred.FacilityId, Operation = "MonthWiseSync",
                FetchedBy = User.Identity?.Name ?? "system",
                RecordsFetched = facSaved, Status = "Success",
                ResponseSummary = $"{facSaved} saved, {facFiles} downloaded, {facDups} dup — {monthChunks.Count} months"
            });
            await _db.SaveChangesAsync(ct);

            await Send(new { status = "facility_done", message = $"[{facName}] ✓ {facSaved} new, {facDups} dup, {facFiles} files", facilityIndex, facilityFi = fi, facilityName = facName, saved = facSaved, dups = facDups, files = facFiles, pct = (int)((double)stepsDone / totalSteps * 100) });
        }

        heartbeatCts.Cancel();
        _cache.Remove("statusbar_static");
        ActiveSyncState.Finish(grandSaved, grandFiles);
        await Send(new { status = "done", message = $"Complete — {grandSaved} new records, {grandDups} dup, {grandFiles} files · {creds.Count} facilities · {monthChunks.Count} months", grandTotal, grandSaved, grandDups, grandFiles });
    }

    // ── Background Download Service — trigger + status ────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult TriggerPendingDownload()
    {
        PendingDownloadState.Trigger();
        return Json(new { ok = true, message = "Background download triggered." });
    }

    [HttpGet]
    public IActionResult PendingDownloadStatusApi()
    {
        var s = PendingDownloadState.Get();
        return Json(new
        {
            isRunning       = s.IsRunning,
            total           = s.Total,
            done            = s.Done,
            failed          = s.Failed,
            currentFacility = s.CurrentFacility,
            pct             = s.Total > 0 ? (int)((double)(s.Done + s.Failed) / s.Total * 100) : 0,
            startedAt       = s.StartedAt == default ? (DateTime?)null : s.StartedAt,
            finishedAt      = s.FinishedAt,
            lastRunAt       = s.LastRunAt,
            lastDone        = s.LastDone
        });
    }

    // ── Download Facility XML as ZIP ──────────────────────────────

    [HttpGet]
    public async Task<IActionResult> DownloadFacilityXml(int facilityId, string? types = "2,8")
    {
        var typeList = (types ?? "2,8")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).ToList();

        var records = await _db.PortalTransactions
            .AsNoTracking()
            .Where(t => t.FacilityId == facilityId
                     && t.FileDownloaded
                     && t.FileContentXml != null
                     && typeList.Contains(t.Type))
            .Select(t => new { t.FileName, t.TransactionId, t.Type, t.FileContentXml, t.SyncPeriod })
            .ToListAsync();

        if (!records.Any())
            return NotFound("No downloaded XML files found for the selected types and facility.");

        var facility = await _db.Facilities.FindAsync(facilityId);
        var safeFac  = (facility?.Name ?? $"facility-{facilityId}")
            .Replace(' ', '_').Replace('/', '_').Replace('\\', '_');

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var r in records)
            {
                var folder = r.Type switch { "2" => "Claims", "8" => "Remittance", "16" => "PA_Requests", "32" => "PA_Auth", _ => "Other" };
                var period = string.IsNullOrWhiteSpace(r.SyncPeriod) ? "misc" : r.SyncPeriod;
                var fn = string.IsNullOrWhiteSpace(r.FileName)
                    ? $"{r.TransactionId}.xml"
                    : r.FileName.Replace('/', '_').Replace('\\', '_');
                var entry = zip.CreateEntry($"{folder}/{period}/{fn}", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                await writer.WriteAsync(r.FileContentXml);
            }
        }

        var typeLabel = string.Join("-", typeList.Select(t => t switch { "2" => "claims", "8" => "remittance", "16" => "pa-req", "32" => "pa-auth", _ => $"type{t}" }));
        return File(ms.ToArray(), "application/zip", $"{safeFac}-{typeLabel}.zip");
    }

    // ── Download Pending Files — SSE stream ───────────────────────

    [HttpGet]
    public async Task DownloadPendingStream(int? facilityId = null, int resumeFromId = 0)
    {
        var ct = HttpContext.RequestAborted;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.WriteAsync("retry: 30000\n\n");
        await Response.Body.FlushAsync(ct);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try { while (!heartbeatCts.Token.IsCancellationRequested) { await Task.Delay(20_000, heartbeatCts.Token); await Response.WriteAsync(": ping\n\n", heartbeatCts.Token); await Response.Body.FlushAsync(heartbeatCts.Token); } } catch { }
        }, heartbeatCts.Token);

        async Task Send(object obj)
        {
            if (ct.IsCancellationRequested) return;
            try { await Response.WriteAsync($"data: {JsonSerializer.Serialize(obj)}\n\n", ct); await Response.Body.FlushAsync(ct); } catch { }
        }

        // Build pending query
        var query = _db.PortalTransactions.Include(t => t.Facility)
            .Where(t => !t.FileDownloaded && t.FileId != null && t.Portal == "DHA");
        if (facilityId.HasValue) query = query.Where(t => t.FacilityId == facilityId.Value);
        if (resumeFromId > 0)   query = query.Where(t => t.Id > resumeFromId);

        var pending = await query.OrderBy(t => t.FacilityId).ThenBy(t => t.Id).ToListAsync(ct);
        int total   = pending.Count;

        if (total == 0)
        {
            await Send(new { status = "done", message = "No pending files to download.", done = 0, failed = 0, total = 0, lastId = resumeFromId });
            heartbeatCts.Cancel(); return;
        }

        await Send(new { status = "start", message = $"{total} pending files to download", total });
        if (resumeFromId > 0)
            await Send(new { status = "resumed", message = $"Resuming after record #{resumeFromId}", resumeFromId });

        // Cache credentials by facilityId to avoid repeated DB calls
        var credCache = new Dictionary<int, (string username, string pwd)>();
        int done = 0, failed = 0, lastId = resumeFromId;

        foreach (var tx in pending)
        {
            if (ct.IsCancellationRequested) break;

            if (!credCache.TryGetValue(tx.FacilityId, out var cred))
            {
                var cr = await _db.PortalCredentials
                    .FirstOrDefaultAsync(c => c.FacilityId == tx.FacilityId && c.IsActive && c.Portal == "DHA", ct);
                if (cr == null) { failed++; lastId = tx.Id; await Send(new { status = "skip", txId = tx.Id, facilityId = tx.FacilityId, message = "No credentials", done, failed, total }); continue; }
                try   { cred = (cr.Username, Encoding.UTF8.GetString(Convert.FromBase64String(cr.PasswordEncrypted))); credCache[tx.FacilityId] = cred; }
                catch { failed++; lastId = tx.Id; continue; }
            }

            try
            {
                var (_, dlFileName, dlBytes, dlErr) = await _dha.DownloadTransactionFileAsync(cred.username, cred.pwd, tx.FileId!);
                if (dlErr == null && dlBytes?.Length > 0)
                {
                    var (contentXml, _) = DhaPortalService.ParseDownloadedFile(dlBytes);
                    await _db.PortalTransactions.Where(t => t.Id == tx.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.FileDownloaded,   true)
                            .SetProperty(t => t.FileContentXml,   contentXml)
                            .SetProperty(t => t.FileSizeBytes,    (long?)dlBytes.Length)
                            .SetProperty(t => t.FileDownloadedAt, DateTime.UtcNow), ct);
                    done++;
                }
                else { failed++; }
            }
            catch { failed++; }

            lastId = tx.Id;
            int pct = (int)((double)(done + failed) / total * 100);
            await Send(new { status = "progress", txId = tx.Id, facilityName = tx.Facility?.Name ?? $"Facility {tx.FacilityId}", done, failed, total, pct, lastId });
        }

        heartbeatCts.Cancel();
        _cache.Remove("statusbar_static");
        await Send(new { status = "done", message = $"Complete — {done} downloaded · {failed} failed", done, failed, total, lastId });
    }

    // ── Live Status Bar API ────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> StatusBar()
    {
        var syncState = ActiveSyncState.Get();
        const string cacheKey = "statusbar_static";

        // Static counts change slowly — cache for 20 s.
        // Invalidated immediately when a sync completes (syncRunning just flipped to false).
        if (!syncState.IsRunning && _cache.TryGetValue(cacheKey, out object? cached))
            return Json(BuildStatusBarJson(cached!, syncState, User.Identity?.Name));

        // Fetch all static values in two queries (one for transactions, one for credentials)
        var txStats = await _db.PortalTransactions
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Files = g.Count(t => t.FileContentXml != null) })
            .FirstOrDefaultAsync();

        var credStats = await _db.PortalCredentials
            .AsNoTracking()
            .Where(c => c.IsActive)
            .GroupBy(c => c.Portal)
            .Select(g => new { Portal = g.Key, Count = g.Count() })
            .ToListAsync();

        var facCount = await _db.Facilities.CountAsync(f => f.IsActive);

        var lastLog = await _db.PortalFetchLogs
            .AsNoTracking()
            .OrderByDescending(l => l.FetchedAt)
            .Select(l => new { l.FetchedAt, l.Status, l.Operation, l.Portal })
            .FirstOrDefaultAsync();

        var staticData = new
        {
            txCount        = txStats?.Total    ?? 0,
            fileCount      = txStats?.Files    ?? 0,
            facCount,
            credCount      = credStats.Sum(c => c.Count),
            dhaCreds       = credStats.FirstOrDefault(c => c.Portal == "DHA")?.Count ?? 0,
            rhaCreds       = credStats.FirstOrDefault(c => c.Portal == "RHA")?.Count ?? 0,
            lastSyncTime   = lastLog?.FetchedAt.ToString("dd MMM yyyy HH:mm"),
            lastSyncStatus = lastLog?.Status,
            lastSyncOp     = lastLog?.Operation,
            lastSyncPortal = lastLog?.Portal
        };

        _cache.Set(cacheKey, (object)staticData, TimeSpan.FromSeconds(20));
        return Json(BuildStatusBarJson(staticData, syncState, User.Identity?.Name));
    }

    private static object BuildStatusBarJson(object staticData, SyncSnapshot sync, string? user)
    {
        // Merge static DB snapshot with live sync state (not cached)
        dynamic d = staticData;
        return new
        {
            txCount        = (int)d.txCount,
            fileCount      = (int)d.fileCount,
            facCount       = (int)d.facCount,
            credCount      = (int)d.credCount,
            dhaCreds       = (int)d.dhaCreds,
            rhaCreds       = (int)d.rhaCreds,
            lastSyncTime   = (string?)d.lastSyncTime,
            lastSyncStatus = (string?)d.lastSyncStatus,
            lastSyncOp     = (string?)d.lastSyncOp,
            lastSyncPortal = (string?)d.lastSyncPortal,
            serverTime     = DateTime.Now.ToString("HH:mm:ss"),
            serverDate     = DateTime.Today.ToString("dd MMM yyyy"),
            user           = user ?? "—",
            syncRunning         = sync.IsRunning,
            syncPct             = sync.Pct,
            syncFacility        = sync.CurrentFacility,
            syncMonth           = sync.CurrentMonth,
            syncFacilityIndex   = sync.FacilityIndex,
            syncTotalFacilities = sync.TotalFacilities,
            syncRecordsSaved    = sync.RecordsSaved,
            syncFilesDownloaded = sync.FilesDownloaded
        };
    }

    // ── Helpers ────────────────────────────────────────────────────

    private async Task<PortalFetchViewModel> BuildFetchVmAsync()
    {
        var facilities = await _db.Facilities.Where(f => f.IsActive).AsNoTracking().ToListAsync();
        var logs = await _db.PortalFetchLogs
            .Include(l => l.Facility)
            .AsNoTracking()
            .OrderByDescending(l => l.FetchedAt)
            .Take(10)
            .ToListAsync();

        return new PortalFetchViewModel
        {
            Facilities = facilities.Select(f => new SelectListItem(f.Name, f.Id.ToString())).ToList(),
            RecentLogs = logs
        };
    }

    private async Task<PortalSyncViewModel> BuildSyncVmAsync()
    {
        var facilities    = await _db.Facilities.Where(f => f.IsActive).AsNoTracking().ToListAsync();
        var totalInDb     = await _db.PortalTransactions.CountAsync();
        var totalFiles    = await _db.PortalTransactions.CountAsync(t => t.FileDownloaded);

        return new PortalSyncViewModel
        {
            Facilities       = facilities.Select(f => new SelectListItem(f.Name, f.Id.ToString())).ToList(),
            TotalInDb        = totalInDb,
            TotalFilesInDb   = totalFiles
        };
    }

    private static List<(DateTime Start, DateTime End, string Label)> BuildMonthChunks(DateTime from, DateTime to)
    {
        var chunks = new List<(DateTime, DateTime, string)>();
        var cur = from;
        while (cur <= to)
        {
            var end = new DateTime(cur.Year, cur.Month, DateTime.DaysInMonth(cur.Year, cur.Month));
            if (end > to) end = to;
            chunks.Add((cur, end, cur.ToString("MMM yyyy")));
            cur = end.AddDays(1);
        }
        return chunks;
    }
}
