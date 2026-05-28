using Analytika.Models.ViewModels;

namespace Analytika.Services;

public interface IRhaPortalService
{
    Task<(string? token, string? error)> AuthenticateAsync(string username, string password, string baseUrl, string? apiKey = null);
    Task<(List<PortalFetchResultRow> rows, string? error)> GetClaimsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null);
    Task<(List<PortalFetchResultRow> rows, string? error)> GetRemittancesAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null);
    Task<(List<PortalFetchResultRow> rows, string? error)> GetPriorAuthorizationsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null);
}
