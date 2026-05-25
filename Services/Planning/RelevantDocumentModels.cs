using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class RelevantDocumentMatch
{
    public PlanningDocument Document { get; set; } = null!;

    public List<RelevantDocumentSnippet> Snippets { get; set; } = [];

    public int MatchCount { get; set; }
}

public sealed class RelevantDocumentSnippet
{
    public string HighlightedHtml { get; set; } = string.Empty;

    public int MatchCount { get; set; }
}
