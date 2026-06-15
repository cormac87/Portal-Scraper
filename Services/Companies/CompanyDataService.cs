using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;
using PortalScraper.Services.Planning;

namespace PortalScraper.Services.Companies;

public sealed class CompanyDataService(IDbContextFactory<ApplicationDbContext> dbFactory) : ICompanyDataService
{
    public async Task<int> GetCompanyCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Companies.CountAsync(cancellationToken);
    }

    public async Task<CompanySearchPage> SearchCompaniesAsync(
        CompanySearchFilters filters,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var normalizedPageSize = Math.Max(1, pageSize);
        var currentPage = Math.Max(1, page);
        var query = CompanyQuery.ApplyFilters(db.Companies.AsNoTracking(), filters);
        var companies = await CompanyQuery.ApplyPageSort(query)
            .Skip(PlanningPagination.GetPageSkip(currentPage, normalizedPageSize))
            .Take(normalizedPageSize + 1)
            .Select(company => new Company
            {
                Id = company.Id,
                CompanyName = company.CompanyName,
                CompanyNumber = company.CompanyNumber,
                Email = company.Email,
                PhoneNumber = company.PhoneNumber,
                RegAddressAddressLine1 = company.RegAddressAddressLine1,
                RegAddressAddressLine2 = company.RegAddressAddressLine2,
                RegAddressPostTown = company.RegAddressPostTown,
                RegAddressCounty = company.RegAddressCounty,
                RegAddressPostCode = company.RegAddressPostCode,
                CompanyCategory = company.CompanyCategory,
                CompanyStatus = company.CompanyStatus,
                IncorporationDate = company.IncorporationDate,
                SicCodeSicText1 = company.SicCodeSicText1,
                SicCodeSicText2 = company.SicCodeSicText2,
                SicCodeSicText3 = company.SicCodeSicText3,
                SicCodeSicText4 = company.SicCodeSicText4
            })
            .ToListAsync(cancellationToken);
        var hasNextPage = companies.Count > normalizedPageSize;

        return new CompanySearchPage(
            companies.Take(normalizedPageSize).ToList(),
            TotalCount: null,
            currentPage,
            hasNextPage);
    }

    public async Task<IReadOnlyList<CompanySicCodeOption>> GetSicCodeOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var sicTexts = await db.Companies
            .AsNoTracking()
            .Select(company => company.SicCodeSicText1)
            .Concat(db.Companies.AsNoTracking().Select(company => company.SicCodeSicText2))
            .Concat(db.Companies.AsNoTracking().Select(company => company.SicCodeSicText3))
            .Concat(db.Companies.AsNoTracking().Select(company => company.SicCodeSicText4))
            .Where(sicText => sicText != null && sicText != string.Empty)
            .Distinct()
            .ToListAsync(cancellationToken);

        return CompanyQuery.NormalizeSicCodeOptions(sicTexts).ToList();
    }

    public async Task<Company?> GetCompanyAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(company => company.Id == companyId, cancellationToken);
    }
}
