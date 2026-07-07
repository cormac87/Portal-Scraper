using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public interface IPlanningCompanyHouseNameMatchService
{
    Task<PlanningCompanyHouseNameMatchResult> FindCompanyHouseNamesAsync(
        PlanningApplication application,
        CancellationToken cancellationToken = default);

    Task<PlanningCompanyHouseNameMatchResult> MatchCandidateNamesAsync(
        IReadOnlyCollection<PlanningCompanyHouseNameCandidate> candidates,
        int processedDocumentCount,
        int skippedDocumentCount,
        CancellationToken cancellationToken = default);
}
