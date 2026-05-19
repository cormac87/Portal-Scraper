namespace PortalScraper.Data;

public sealed class PlanningSearchResult
{
    public string MatchType { get; set; } = string.Empty;

    public Guid PlanningApplicationId { get; set; }

    public Guid? PlanningDocumentId { get; set; }

    public string PlanningAuthorityName { get; set; } = string.Empty;

    public string? ApplicationReference { get; set; }

    public string ApplicationTitle { get; set; } = string.Empty;

    public string? Status { get; set; }

    public string? Address { get; set; }

    public DateTime? ReceivedDate { get; set; }

    public DateTime? ValidatedDate { get; set; }

    public string? DocumentName { get; set; }

    public string? DocumentType { get; set; }

    public string? Url { get; set; }

    public DateTime? PublishedDate { get; set; }

    public int SearchRank { get; set; }

    public string? PreviewText { get; set; }
}
