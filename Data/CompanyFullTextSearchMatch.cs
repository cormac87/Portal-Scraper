namespace PortalScraper.Data;

public sealed class CompanyFullTextSearchMatch
{
    public Guid CompanyId { get; set; }

    public int SearchRank { get; set; }
}
