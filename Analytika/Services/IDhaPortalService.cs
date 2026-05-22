using Analytika.Models.ViewModels;

namespace Analytika.Services;

public interface IDhaPortalService
{
    // GetNewTransactions — returns unacknowledged queue items
    Task<(int count, List<PortalFetchResultRow> rows, string? error)> GetNewTransactionsAsync(string login, string pwd);

    // GetNewPriorAuthorizationTransactions
    Task<(int count, List<PortalFetchResultRow> rows, string? error)> GetNewPriorAuthorizationsAsync(string login, string pwd);

    // SearchTransactions — direction: 1=sent, 2=received; status: 1=new, 2=downloaded; dates: "dd/MM/yyyy HH:mm:ss"
    // transactionId: 2=Claim, 8=Remittance, 16=PA.Request, 32=PA.Authorization (portal rejects -1/"all")
    Task<(int result, List<PortalFetchResultRow> rows, string? error)> SearchTransactionsAsync(
        string login, string pwd, int direction, string? fromDate, string? toDate,
        int transactionStatus, int transactionId = 2, int minRecord = -1, int maxRecord = -1);

    // SearchTransactions on archive endpoint (for data >24 months old)
    Task<(int result, List<PortalFetchResultRow> rows, string? error)> SearchTransactionsArchiveAsync(
        string login, string pwd, int direction, string? fromDate, string? toDate,
        int transactionStatus, int transactionId = 2, int minRecord = -1, int maxRecord = -1);

    // DownloadTransactionFile — fileId is the FileID attribute from <File> element
    Task<(int result, string? fileName, byte[]? fileBytes, string? error)> DownloadTransactionFileAsync(string login, string pwd, string fileId);

    // DownloadTransactionFile on archive endpoint
    Task<(int result, string? fileName, byte[]? fileBytes, string? error)> DownloadTransactionFileArchiveAsync(string login, string pwd, string fileId);

    // SetTransactionDownloaded — acknowledge file so it won't appear in GetNewTransactions again
    Task<(int result, string? error)> SetTransactionDownloadedAsync(string login, string pwd, string fileId);
}
