namespace PortalScraper.Services.Companies;

public interface ICompanyLocationService
{
    Task<CompanyLocationCacheStats> GetCacheStatsAsync(CancellationToken cancellationToken = default);

    Task<CompanyLocationRefreshResult> RefreshCompanyLocationsAsync(
        int maxPostcodes,
        CancellationToken cancellationToken = default);
}
