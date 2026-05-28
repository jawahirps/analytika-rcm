using Analytika.Models.ViewModels;

namespace Analytika.Services;

public interface IDashboardService
{
    Task<FacilityStatusViewModel> BuildFacilityStatusAsync();
    Task<RCMDashboardViewModel> BuildRcmDashboardAsync(string tab, RcmDashboardFilters filters);
}
