using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

public interface ICompanyDataService
{
    Task<int> GetCompanyCountAsync(CancellationToken cancellationToken = default);

    Task<CompanySearchPage> SearchCompaniesAsync(
        CompanySearchFilters filters,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanySicCodeOption>> GetSicCodeOptionsAsync(
        CancellationToken cancellationToken = default);

    Task<Company?> GetCompanyAsync(Guid companyId, CancellationToken cancellationToken = default);
}
