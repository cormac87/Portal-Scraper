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
        var query = CompanyQuery.ApplyFilters(db.Companies.AsNoTracking(), filters);
        var totalCount = await query.CountAsync(cancellationToken);
        var currentPage = PlanningPagination.ClampPage(page, totalCount, normalizedPageSize);
        var companies = await CompanyQuery.ApplyDefaultSort(query)
            .Skip(PlanningPagination.GetPageSkip(currentPage, normalizedPageSize))
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);

        return new CompanySearchPage(companies, totalCount, currentPage);
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
