using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public interface IRelevantPlanningDocumentService
{
    List<RelevantDocumentMatch> BuildMatches(
        IEnumerable<PlanningDocument> documents,
        IReadOnlyList<FullTextSearchCriterion> criteria);
}
