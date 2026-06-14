using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class PlanningSearchCriterionInput
{
    public string Text { get; set; } = string.Empty;

    public List<PlanningSearchKeywordInput> OrKeywords { get; set; } = [];

    public bool RequireAdjacentWords { get; set; }
}

public sealed class PlanningSearchKeywordInput
{
    public string Text { get; set; } = string.Empty;
}

public sealed class FullTextSearchCriterion
{
    public string DisplayText { get; set; } = string.Empty;

    public string SearchCondition { get; set; } = string.Empty;

    public List<string> SearchTerms { get; set; } = [];

    public List<FullTextSearchCriterionAlternative> Alternatives { get; set; } = [];

    public bool RequireAdjacentWords { get; set; }
}

public sealed class FullTextSearchCriterionAlternative
{
    public string DisplayText { get; set; } = string.Empty;

    public string SearchCondition { get; set; } = string.Empty;

    public List<string> SearchTerms { get; set; } = [];
}

public sealed record PlanningApplicationSearchFilters(
    IReadOnlyCollection<Guid>? PlanningAuthorityIds = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null)
{
    public static PlanningApplicationSearchFilters Empty { get; } = new();

    public bool HasActive => PlanningAuthorityIds is not null || StartDate.HasValue || EndDate.HasValue;
}

public sealed record PlanningKeywordSearchPage(
    IReadOnlyList<PlanningSearchResult> Results,
    int TotalCount,
    int CurrentPage,
    bool IsFullTextSearchAvailable);
