using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public interface IPlanningLlmCompanyExtractionService
{
    Task<PlanningLlmCompanyExtractionResult> FindCompaniesAsync(
        PlanningApplication application,
        CancellationToken cancellationToken = default);
}
