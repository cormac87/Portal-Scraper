namespace PortalScraper.Services.Planning;

public interface IPlanningSearchService
{
    List<FullTextSearchCriterion> BuildCriteria(IEnumerable<PlanningSearchCriterionInput> criteria);

    string FormatCriteriaSummary(IReadOnlyList<FullTextSearchCriterion> criteria);

    Task<PlanningKeywordSearchPage> SearchAsync(
        IReadOnlyList<FullTextSearchCriterion> criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
