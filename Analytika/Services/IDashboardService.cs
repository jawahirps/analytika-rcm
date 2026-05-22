using Analytika.Models.ViewModels;

namespace Analytika.Services;

public interface IDashboardService
{
    Task<FacilityStatusViewModel> BuildFacilityStatusAsync();
    RCMDashboardViewModel BuildRcmDashboard(string tab);
}
