using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;
using PortalScraper.Services.Planning;

namespace PortalScraper.Services.Companies;

public sealed class CompanyDataService(IDbContextFactory<ApplicationDbContext> dbFactory) : ICompanyDataService
{
    public async Task<int> GetCompanyCountAsync(CancellationToken cancellationToken = default)
    {
        return await GetCompanyCountAsync(CompanySearchFilters.Empty, cancellationToken);
    }

    public async Task<int> GetCompanyCountAsync(
        CompanySearchFilters filters,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (CompanyQuery.RequiresFullTextSearch(filters)
            && !await CompanyQuery.IsFullTextSearchAvailableAsync(db, cancellationToken))
        {
            throw new InvalidOperationException("Company name search requires SQL Server Full-Text Search and the company full-text index.");
        }

        return await CompanyQuery
            .CreateSearchQuery(db, filters)
            .CountAsync(cancellationToken);
    }

    public async Task<CompanySearchPage> SearchCompaniesAsync(
        CompanySearchFilters filters,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (CompanyQuery.RequiresFullTextSearch(filters)
            && !await CompanyQuery.IsFullTextSearchAvailableAsync(db, cancellationToken))
        {
            return new CompanySearchPage([], TotalCount: 0, CurrentPage: 1, HasNextPage: false, IsFullTextSearchAvailable: false);
        }

        var normalizedPageSize = Math.Max(1, pageSize);
        var currentPage = Math.Max(1, page);
        var query = CompanyQuery.CreateSearchQuery(db, filters);
        var origin = filters.Location is null ? null : CompanyQuery.CreatePoint(filters.Location);
        var companies = await CompanyQuery.ApplyPageSort(query, filters)
            .Skip(PlanningPagination.GetPageSkip(currentPage, normalizedPageSize))
            .Take(normalizedPageSize + 1)
            .Select(result => new Company
            {
                Id = result.Company.Id,
                CompanyName = result.Company.CompanyName,
                CompanyNumber = result.Company.CompanyNumber,
                Email = result.Company.Email,
                PhoneNumber = result.Company.PhoneNumber,
                RegAddressAddressLine1 = result.Company.RegAddressAddressLine1,
                RegAddressAddressLine2 = result.Company.RegAddressAddressLine2,
                RegAddressPostTown = result.Company.RegAddressPostTown,
                RegAddressCounty = result.Company.RegAddressCounty,
                RegAddressPostCode = result.Company.RegAddressPostCode,
                Latitude = result.Company.Latitude,
                Longitude = result.Company.Longitude,
                LocationLookupStatus = result.Company.LocationLookupStatus,
                CompanyCategory = result.Company.CompanyCategory,
                CompanyStatus = result.Company.CompanyStatus,
                IncorporationDate = result.Company.IncorporationDate,
                SicCodeSicText1 = result.Company.SicCodeSicText1,
                SicCodeSicText2 = result.Company.SicCodeSicText2,
                SicCodeSicText3 = result.Company.SicCodeSicText3,
                SicCodeSicText4 = result.Company.SicCodeSicText4,
                DistanceKm = origin == null || result.CompanyLocation == null
                    ? null
                    : result.CompanyLocation.Distance(origin) / 1000d
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
