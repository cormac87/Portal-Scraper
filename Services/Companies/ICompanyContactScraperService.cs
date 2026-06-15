namespace PortalScraper.Services.Companies;

public interface ICompanyContactScraperService
{
    Task<CompanyContactScrapeResult> ScrapeContactsAsync(
        CompanySearchFilters filters,
        CancellationToken cancellationToken = default);
}
