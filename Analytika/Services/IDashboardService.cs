using Analytika.Models.ViewModels;

namespace Analytika.Services;

public interface IDashboardService
{
    Task<FacilityStatusViewModel> BuildFacilityStatusAsync();
    Task<RCMDashboardViewModel> BuildRcmDashboardAsync(string tab, RcmDashboardFilters filters);

    /// <summary>
    /// Force a full aggregation NOW and populate the cache. For startup pre-warm.
    /// This blocks until the aggregation is done — do not call from a request path.
    /// </summary>
    Task WarmAsync(CancellationToken ct = default);
}
