using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public interface IPlanningDataService
{
    Task<PlanningOverviewPage> GetOverviewPageAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task<PlanningApplication?> GetApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default);
}
