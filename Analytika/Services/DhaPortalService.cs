using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Analytika.Models.ViewModels;
using Microsoft.Extensions.Caching.Memory;

namespace Analytika.Services;

/// <summary>
/// DHPO eClaimLink Web Service client.
/// Specification: DHPO WebService Specification V3.0 (2017-07-04)
/// Production endpoint: https://dhpo.eclaimlink.ae/ValidateTransactions.asmx
/// Archive endpoint:    https://dhpo.eclaimlink.ae/ClaimsAndAuthorizationsArchive.asmx
///
/// KEY FACTS from spec:
///  - All responses wrap transaction lists as <Files><File FileID='' FileName='' .../></Files> (XML ATTRIBUTES, not elements)
///  - SearchTransactions direction: 1=sent, 2=received
///  - SearchTransactions transactionStatus: 1=new only, 2=already-downloaded only
///  - SearchTransactions transactionID: -1=all types, 2=Claim, 4=Person.Register, 8=Remittance, 16=PA.Request, 32=PA.Authorization
///  - SearchTransactions date format: "dd/MM/yyyy HH:mm:ss"   (NOT yyyy-MM-dd)
///  - SearchTransactions minRecordCount/maxRecordCount: -1=no filter; filters by # of claims INSIDE the file
///  - SearchTransactions max 500 files returned; date range must not exceed 100 days
///  - GetNewTransactions output field: xmlTransactions (NOT xmlTransaction)
///  - DownloadTransactionFile: fileID parameter = FileID attribute from <File> element
///  - Return codes: 0=OK, 1=warnings, -1=login failed, -3=invalid param, -5=date range >100 days, -6=file not found, -10=no criteria
/// </summary>
public class DhaPortalService : IDhaPortalService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    // Primary (active) endpoint
    private const string PrimaryUrl = "https://dhpo.eclaimlink.ae/ValidateTransactions.asmx";
    // Archive endpoint — for claims >24 months old, auth/PA >6 months old
    private const string ArchiveUrl = "https://dhpo.eclaimlink.ae/ClaimsAndAuthorizationsArchive.asmx";
    private const string SoapNs = "http://www.eClaimLink.ae/";

    // SearchTransactions direction values (per spec)
    public const int DirectionSent = 1;
    public const int DirectionReceived = 2;

    // SearchTransactions transactionStatus values (per spec)
    public const int StatusNew = 1;
    public const int StatusDownloaded = 2;

    // SearchTransactions transactionID values (per spec)
    public const int TxTypeAll = -1;
    public const int TxTypeClaim = 2;
    public const int TxTypePersonRegister = 4;
    public const int TxTypeRemittance = 8;
    public const int TxTypePriorRequest = 16;
    public const int TxTypePriorAuth = 32;

    // Standard set used for bulk sync: Claims, Remittances, PA Requests, PA Authorizations
    public static readonly int[] DefaultTxTypes = [TxTypeClaim, TxTypeRemittance, TxTypePriorRequest, TxTypePriorAuth];

    /// <summary>Canonical record type from the DHPO transaction-type code (authoritative — the search was scoped to this type).</summary>
    public static string TxTypeName(int txType) => txType switch
    {
        TxTypeClaim        => "Claim",
        TxTypeRemittance   => "Remittance",
        TxTypePriorRequest => "Prior Request",
        TxTypePriorAuth    => "Prior Authorization",
        _                  => "Other"
    };

    public DhaPortalService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    // ── SOAP plumbing ──────────────────────────────────────────────

    private string BuildEnvelope(string action, string bodyXml) =>
        $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:tns=""{SoapNs}"">
  <soap:Body>
    <tns:{action}>
      {bodyXml}
    </tns:{action}>
  </soap:Body>
</soap:Envelope>";

    private async Task<XDocument?> CallSoapAsync(string action, string bodyXml, bool useArchive = false)
    {
        var url = useArchive ? ArchiveUrl : PrimaryUrl;
        var client = _httpClientFactory.CreateClient("DHA");
        var content = new StringContent(BuildEnvelope(action, bodyXml), Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{SoapNs}{action}\"");
        try
        {
            var resp = await client.PostAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();
            return XDocument.Parse(body);
        }
        catch { return null; }
    }

    private static string CooldownKey(string login) => $"dha-auth-cooldown:{login.Trim().ToLowerInvariant()}";

    private bool TryGetCooldownError(string login, out string? error)
    {
        if (_cache.TryGetValue(CooldownKey(login), out DateTimeOffset retryAfter))
        {
            var remaining = retryAfter - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                var minutes = Math.Ceiling(remaining.TotalSeconds / 60.0);
                error = $"DHPO authentication is rate-limited for this account. Please wait about {minutes:N0} minute(s) before retrying.";
                return true;
            }
        }

        error = null;
        return false;
    }

    private void StartCooldown(string login, TimeSpan? duration = null)
    {
        var retryAfter = DateTimeOffset.UtcNow.Add(duration ?? TimeSpan.FromMinutes(1));
        _cache.Set(CooldownKey(login), retryAfter, retryAfter);
    }

    private static bool IsAuthThrottle(int code, string? error)
        => code == -999 || (error?.Contains("too many authentication attempts", StringComparison.OrdinalIgnoreCase) ?? false);

    // ── GetNewTransactions ─────────────────────────────────────────
    // Returns files NOT yet flagged as downloaded from the DHPO queue.
    // Response element: xmlTransactions (XML attribute-based <Files><File .../></Files>)
    // NOTE: Do NOT use this for Prior Auth — use GetNewPriorAuthorizationTransactions.

    public async Task<(int count, List<PortalFetchResultRow> rows, string? error)> GetNewTransactionsAsync(
        string login, string pwd)
    {
        if (TryGetCooldownError(login, out var cooldownError))
            return (0, new(), cooldownError);

        var body = $"<tns:login>{login}</tns:login><tns:pwd>{pwd}</tns:pwd>";
        var doc = await CallSoapAsync("GetNewTransactions", body);
        if (doc == null) return (0, new(), "Connection failed");

        XNamespace ns = SoapNs;
        var resultStr = FirstDescendantValue(doc, ns, "GetNewTransactionsResult");
        var error = FirstDescendantValue(doc, ns, "errorMessage");
        // SPEC: output element is "xmlTransactions" (plural)
        var xml = FirstDescendantValue(doc, ns, "xmlTransactions");

        var rows = ParseFilesXml(xml);
        int.TryParse(resultStr, out var count);
        if (IsAuthThrottle(count, error))
        {
            StartCooldown(login);
            return (0, rows, $"GetNewTransactions error code {count}: {error}");
        }
        if (count < 0)
            return (0, rows, $"GetNewTransactions error code {count}: {error}");
        if (count == 0) count = rows.Count;

        return (count, rows, string.IsNullOrEmpty(error) ? null : error);
    }

    // ── GetNewPriorAuthorizationTransactions ───────────────────────

    public async Task<(int count, List<PortalFetchResultRow> rows, string? error)> GetNewPriorAuthorizationsAsync(
        string login, string pwd)
    {
        if (TryGetCooldownError(login, out var cooldownError))
            return (0, new(), cooldownError);

        var body = $"<tns:login>{login}</tns:login><tns:pwd>{pwd}</tns:pwd>";
        var doc = await CallSoapAsync("GetNewPriorAuthorizationTransactions", body);
        if (doc == null) return (0, new(), "Connection failed");

        XNamespace ns = SoapNs;
        var resultStr = FirstDescendantValue(doc, ns, "GetNewPriorAuthorizationTransactionsResult");
        var error = FirstDescendantValue(doc, ns, "errorMessage");
        var xml = FirstDescendantValue(doc, ns, "xmlTransaction");

        var rows = ParseFilesXml(xml);
        int.TryParse(resultStr, out var count);
        if (IsAuthThrottle(count, error))
        {
            StartCooldown(login);
            return (0, rows, $"GetNewPriorAuthorizationTransactions error code {count}: {error}");
        }
        return (count == 0 ? rows.Count : count, rows, string.IsNullOrEmpty(error) ? null : error);
    }

    // ── SearchTransactions ─────────────────────────────────────────
    // Searches sent/received transactions within a date range.
    //
    // direction:          1 = sent, 2 = received          (per spec)
    // transactionID:     -1 = all, 2=Claim, 8=Remittance  (per spec, bitwise OR)
    // transactionStatus:  1 = new only, 2 = already downloaded (per spec)
    // Date format:        "dd/MM/yyyy HH:mm:ss"           (per spec)
    // Record counts:     -1 = no filter                   (per spec)
    // Max results:        500 per call                    (per spec)
    // Max date range:     100 days                        (per spec — error code -5)

    public async Task<(int result, List<PortalFetchResultRow> rows, string? error)> SearchTransactionsAsync(
        string login, string pwd,
        int direction,            // 1=sent, 2=received
        string? fromDate,         // "dd/MM/yyyy HH:mm:ss" or null
        string? toDate,           // "dd/MM/yyyy HH:mm:ss" or null
        int transactionStatus,    // 1=new, 2=downloaded
        int transactionId = 2,    // 2=Claim, 8=Remittance, 16=PA.Request, 32=PA.Authorization (portal rejects -1)
        int minRecord = -1,       // -1 → uses 1 (portal rejects -1)
        int maxRecord = -1)       // -1 → uses 500 (portal rejects -1)
    {
        if (TryGetCooldownError(login, out var cooldownError))
            return (0, new(), cooldownError);

        // NOTE: The DHA portal rejects minRecordCount/maxRecordCount = -1 despite the spec saying
        //       -1 means "no filter". Use 1/500 as safe stand-ins.
        var minRec = minRecord > 0 ? minRecord : 1;
        var maxRec = maxRecord > 0 ? maxRecord : 500;

        var body = $@"<tns:login>{login}</tns:login>
<tns:pwd>{pwd}</tns:pwd>
<tns:direction>{direction}</tns:direction>
<tns:callerLicense></tns:callerLicense>
<tns:ePartner></tns:ePartner>
<tns:transactionID>{transactionId}</tns:transactionID>
<tns:TransactionStatus>{transactionStatus}</tns:TransactionStatus>
<tns:transactionFileName></tns:transactionFileName>
<tns:transactionFromDate>{fromDate ?? ""}</tns:transactionFromDate>
<tns:transactionToDate>{toDate ?? ""}</tns:transactionToDate>
<tns:minRecordCount>{minRec}</tns:minRecordCount>
<tns:maxRecordCount>{maxRec}</tns:maxRecordCount>";

        var doc = await CallSoapAsync("SearchTransactions", body);
        if (doc == null) return (0, new(), "Connection failed");

        XNamespace ns = SoapNs;
        var resultStr = FirstDescendantValue(doc, ns, "SearchTransactionsResult");
        var error = FirstDescendantValue(doc, ns, "errorMessage");
        // SPEC: output element is "foundTransactions"
        var xml = FirstDescendantValue(doc, ns, "foundTransactions", "xmlTransactions", "xmlTransaction");

        var rows = ParseFilesXml(xml);
        int.TryParse(resultStr, out var result);
        if (IsAuthThrottle(result, error))
        {
            StartCooldown(login);
            return (0, rows, $"SearchTransactions error code {result}: {error}");
        }
        return (result, rows, string.IsNullOrEmpty(error) ? null : error);
    }

    // ── SearchTransactions — Archive endpoint ──────────────────────
    // Use for claims >24 months old, auth/PA >6 months old.
    // Archive search period must not exceed 1 month at a time.

    public async Task<(int result, List<PortalFetchResultRow> rows, string? error)> SearchTransactionsArchiveAsync(
        string login, string pwd,
        int direction,
        string? fromDate,
        string? toDate,
        int transactionStatus,
        int transactionId = TxTypeClaim,
        int minRecord = -1,
        int maxRecord = -1)
    {
        if (TryGetCooldownError(login, out var cooldownError))
            return (0, new(), cooldownError);

        var minRec = minRecord > 0 ? minRecord : 1;
        var maxRec = maxRecord > 0 ? maxRecord : 500;

        var body = $@"<tns:login>{login}</tns:login>
<tns:pwd>{pwd}</tns:pwd>
<tns:direction>{direction}</tns:direction>
<tns:callerLicense></tns:callerLicense>
<tns:ePartner></tns:ePartner>
<tns:transactionID>{transactionId}</tns:transactionID>
<tns:TransactionStatus>{transactionStatus}</tns:TransactionStatus>
<tns:transactionFileName></tns:transactionFileName>
<tns:transactionFromDate>{fromDate ?? ""}</tns:transactionFromDate>
<tns:transactionToDate>{toDate ?? ""}</tns:transactionToDate>
<tns:minRecordCount>{minRec}</tns:minRecordCount>
<tns:maxRecordCount>{maxRec}</tns:maxRecordCount>";

        var doc = await CallSoapAsync("SearchTransactions", body, useArchive: true);
        if (doc == null) return (0, new(), "Connection failed (Archive)");

        XNamespace ns = SoapNs;
        var resultStr = FirstDescendantValue(doc, ns, "SearchTransactionsResult");
        var error = FirstDescendantValue(doc, ns, "errorMessage");
        var xml = FirstDescendantValue(doc, ns, "foundTransactions", "xmlTransactions", "xmlTransaction");

        var rows = ParseFilesXml(xml);
        int.TryParse(resultStr, out var result);
        if (IsAuthThrottle(result, error))
        {
            StartCooldown(login);
            return (0, rows, $"SearchTransactions error code {result}: {error}");
        }
        return (result, rows, string.IsNullOrEmpty(error) ? null : error);
    }

    // ── DownloadTransactionFile ────────────────────────────────────
    // Downloads the raw file bytes using the FileID from <File FileID=''> attribute.
    // Response: file = byte[] (raw XML file content)

    public async Task<(int result, string? fileName, byte[]? fileBytes, string? error)> DownloadTransactionFileAsync(
        string login, string pwd, string fileId)
    {
        if (TryGetCooldownError(login, out var cooldownError))
            return (0, null, null, cooldownError);

        var body = $"<tns:login>{login}</tns:login><tns:pwd>{pwd}</tns:pwd><tns:fileID>{fileId}</tns:fileID>";
        var doc = await CallSoapAsync("DownloadTransactionFile", body);
        if (doc == null) return (0, null, null, "Connection failed");

        XNamespace ns = SoapNs;
        var resultStr = doc.Descendants(ns + "DownloadTransactionFileResult").FirstOrDefault()?.Value;
        var fileName = doc.Descendants(ns + "fileName").FirstOrDefault()?.Value;
        var fileB64 = doc.Descendants(ns + "file").FirstOrDefault()?.Value;
        var error = doc.Descendants(ns + "errorMessage").FirstOrDefault()?.Value;

        byte[]? fileBytes = null;
        if (!string.IsNullOrWhiteSpace(fileB64))
        {
            try { fileBytes = Convert.FromBase64String(fileB64.Trim()); }
            catch { /* malformed base64 */ }
        }

        int.TryParse(resultStr, out var result);
        if (IsAuthThrottle(result, error))
        {
            StartCooldown(login);
            return (0, fileName, fileBytes, $"DownloadTransactionFile error code {result}: {error}");
        }
        return (result, fileName, fileBytes, string.IsNullOrEmpty(error) ? null : error);
    }

    // ── DownloadTransactionFile — Archive endpoint ─────────────────

    public async Task<(int result, string? fileName, byte[]? fileBytes, string? error)> DownloadTransactionFileArchiveAsync(
        string login, string pwd, string fileId)
    {
        if (TryGetCooldownError(login, out var cooldownError))
            return (0, null, null, cooldownError);

        var body = $"<tns:login>{login}</tns:login><tns:pwd>{pwd}</tns:pwd><tns:fileID>{fileId}</tns:fileID>";
        var doc = await CallSoapAsync("DownloadTransactionFile", body, useArchive: true);
        if (doc == null) return (0, null, null, "Connection failed (Archive)");

        XNamespace ns = SoapNs;
        var resultStr = doc.Descendants(ns + "DownloadTransactionFileResult").FirstOrDefault()?.Value;
        var fileName = doc.Descendants(ns + "fileName").FirstOrDefault()?.Value;
        var fileB64 = doc.Descendants(ns + "file").FirstOrDefault()?.Value;
        var error = doc.Descendants(ns + "errorMessage").FirstOrDefault()?.Value;

        byte[]? fileBytes = null;
        if (!string.IsNullOrWhiteSpace(fileB64))
        {
            try { fileBytes = Convert.FromBase64String(fileB64.Trim()); }
            catch { }
        }

        int.TryParse(resultStr, out var result);
        if (IsAuthThrottle(result, error))
        {
            StartCooldown(login);
            return (0, fileName, fileBytes, $"DownloadTransactionFile error code {result}: {error}");
        }
        return (result, fileName, fileBytes, string.IsNullOrEmpty(error) ? null : error);
    }

    // ── SetTransactionDownloaded ───────────────────────────────────
    // Mark a file as downloaded so GetNewTransactions won't return it again.
    // WSDL element name: fileID — per DHPO specification

    public async Task<(int result, string? error)> SetTransactionDownloadedAsync(
        string login, string pwd, string fileId)
    {
        if (TryGetCooldownError(login, out var cooldownError))
            return (0, cooldownError);

        var body = $"<tns:login>{login}</tns:login><tns:pwd>{pwd}</tns:pwd><tns:fileID>{fileId}</tns:fileID>";
        var doc = await CallSoapAsync("SetTransactionDownloaded", body);
        if (doc == null) return (0, "Connection failed");

        XNamespace ns = SoapNs;
        var resultStr = doc.Descendants(ns + "SetTransactionDownloadedResult").FirstOrDefault()?.Value;
        var error = doc.Descendants(ns + "errorMessage").FirstOrDefault()?.Value;
        int.TryParse(resultStr, out var result);
        if (IsAuthThrottle(result, error))
        {
            StartCooldown(login);
            return (0, $"SetTransactionDownloaded error code {result}: {error}");
        }
        return (result, string.IsNullOrEmpty(error) ? null : error);
    }

    // ── ParseFilesXml ──────────────────────────────────────────────
    // Parses the <Files><File FileID='' FileName='' SenderID='' ReceiverID=''
    //                         TransactionDate='' RecordCount='' IsDownloaded=''/></Files>
    // format returned by both GetNewTransactions and SearchTransactions.
    // ALL data is in XML ATTRIBUTES (not child elements).

    public static List<PortalFetchResultRow> ParseFilesXml(string? xmlStr)
    {
        var rows = new List<PortalFetchResultRow>();
        if (string.IsNullOrWhiteSpace(xmlStr)) return rows;

        try
        {
            var doc = XDocument.Parse(xmlStr);

            foreach (var file in doc.Descendants().Where(e => e.Name.LocalName == "File"))
            {
                string? Attr(params string[] names)
                {
                    foreach (var n in names)
                    {
                        var v = file.Attributes().FirstOrDefault(a => a.Name.LocalName == n)?.Value;
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                    return null;
                }

                // FileID is the key — used as parameter for DownloadTransactionFile
                var fileId = Attr("FileID");
                var fileName = Attr("FileName");

                rows.Add(new PortalFetchResultRow
                {
                    FileId = fileId ?? fileName ?? "-",
                    FileName = fileName,
                    Type = DetermineType(fileName),
                    Status = Attr("IsDownloaded") == "True" ? "Downloaded" : "New",
                    Payer = Attr("ReceiverID"),       // ReceiverID = payer/receiver side
                    Date = Attr("TransactionDate"),
                    Amount = Attr("RecordCount"),       // RecordCount = # claims in file
                    RawXml = file.ToString()
                });
            }
        }
        catch { /* return whatever was parsed */ }

        return rows;
    }

    private static string? FirstDescendantValue(XDocument doc, XNamespace ns, params string[] localNames)
    {
        foreach (var localName in localNames)
        {
            var value = doc.Descendants(ns + localName).FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            value = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    // Infer transaction type from the file name
    private static string DetermineType(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "Claim";
        var fn = fileName.ToLowerInvariant();
        if (fn.Contains("remit") || fn.StartsWith("ra_") || fn.StartsWith("rem"))
            return "Remittance";
        if (fn.Contains("claim")) return "Claim";
        if (fn.Contains("prior") || fn.Contains("auth")) return "Prior Auth";
        if (fn.Contains("person")) return "Person Register";
        if (fn.Contains("prescrip")) return "Prescription";
        return "Claim"; // Default
    }

    // ── ParseDownloadedFile ────────────────────────────────────────
    // Processes the raw byte[] returned by DownloadTransactionFile.
    // The file is the original XML submitted to DHPO (e-claim format).

    public static (string contentXml, List<PortalFetchResultRow> innerRows) ParseDownloadedFile(
        byte[] fileBytes, string? originalFileName = null)
    {
        string contentXml;
        var innerRows = new List<PortalFetchResultRow>();

        try
        {
            contentXml = Encoding.UTF8.GetString(fileBytes);
        }
        catch
        {
            // If not valid UTF-8 (e.g. zip), store as base64
            contentXml = $"<!-- binary file, base64 encoded -->\n{Convert.ToBase64String(fileBytes)}";
            return (contentXml, innerRows);
        }

        // Try to parse the XML and extract individual claim records
        try
        {
            var doc = XDocument.Parse(contentXml);
            var root = doc.Root;
            if (root == null) return (contentXml, innerRows);

            // DHA e-claim XML typically has <Claim> or <Transaction> child elements
            var claimElements = root
                .Descendants()
                .Where(e => e.Name.LocalName is "Claim" or "Transaction" or "PriorRequest"
                                              or "PriorAuthorization" or "Remittance" or "Encounter")
                .Take(1000)
                .ToList();

            foreach (var el in claimElements)
            {
                string? Get(params string[] names)
                {
                    foreach (var n in names)
                    {
                        var v = el.Descendants().FirstOrDefault(x => x.Name.LocalName == n)?.Value
                             ?? el.Attribute(n)?.Value
                             ?? el.Element(n)?.Value;
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                    return null;
                }

                innerRows.Add(new PortalFetchResultRow
                {
                    FileId = Get("ID", "ClaimID", "TransactionID", "id") ?? el.Attribute("ID")?.Value ?? "-",
                    Type = el.Name.LocalName,
                    Status = Get("Status", "ClaimStatus") ?? "-",
                    FileName = originalFileName,
                    Date = Get("Date", "ServiceDate", "SubmissionDate", "TransactionDate"),
                    Payer = Get("PayerID", "Payer", "ReceiverID", "InsuranceCompanyID"),
                    Amount = Get("GrossAmount", "NetAmount", "Amount", "TotalAmount"),
                    RawXml = el.ToString()
                });
            }
        }
        catch { /* keep contentXml, return empty innerRows */ }

        return (contentXml, innerRows);
    }

    // ── FormatDhpoDate ─────────────────────────────────────────────
    // Converts yyyy-MM-dd to the DHPO required format: "dd/MM/yyyy HH:mm:ss"

    public static string? FormatDhpoDate(string? isoDate, bool endOfDay = false)
    {
        if (string.IsNullOrWhiteSpace(isoDate)) return null;

        // Accept yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss
        if (DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            if (endOfDay) dt = dt.Date.AddDays(1).AddSeconds(-1); // 23:59:59
            return dt.ToString("dd/MM/yyyy HH:mm:ss");
        }

        return isoDate; // pass through if already in another format
    }

    // ── Returned value descriptions ────────────────────────────────

    public static string DescribeResult(int code) => code switch
    {
        3 => "No approved trade drugs (prescription not returned)",
        2 => "No new prior auth transactions available",
        1 => "Success with warnings",
        0 => "Success",
        -1 => "Login failed",
        -2 => "Validation failed with errors",
        -3 => "Invalid or missing parameter",
        -4 => "Unexpected server error",
        -5 => "Date range exceeds 100 days",
        -6 => "File not found",
        -7 => "Transaction type not supported",
        -10 => "No search criteria provided",
        _ => $"Unknown result code: {code}"
    };
}
