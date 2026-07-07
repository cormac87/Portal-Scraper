using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public interface IPlanningOrganisationExtractionService
{
    Task<PlanningOrganisationExtractionResult> ExtractOrganisationsAsync(
        PlanningApplication application,
        CancellationToken cancellationToken = default);
}
