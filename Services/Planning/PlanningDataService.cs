using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class PlanningDataService(IDbContextFactory<ApplicationDbContext> dbFactory) : IPlanningDataService
{
    public async Task<PlanningOverviewPage> GetOverviewPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var authorities = await db.PlanningAuthorities
            .AsNoTracking()
            .OrderBy(authority => authority.Name)
            .ToListAsync(cancellationToken);

        var applicationTotalCount = await db.PlanningApplications.CountAsync(cancellationToken);
        var documentTotalCount = await db.PlanningDocuments.CountAsync(cancellationToken);
        var currentPage = PlanningPagination.ClampPage(page, applicationTotalCount, pageSize);

        var applications = await db.PlanningApplications
            .AsNoTracking()
            .Include(application => application.PlanningAuthority)
            .Include(application => application.PlanningDocuments)
            .AsSplitQuery()
            .OrderByDescending(application => application.ScrapedAt)
            .ThenBy(application => application.ApplicationReference)
            .ThenBy(application => application.Id)
            .Skip(PlanningPagination.GetPageSkip(currentPage, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PlanningOverviewPage(
            authorities,
            applications,
            currentPage,
            applicationTotalCount,
            documentTotalCount);
    }

    public async Task<PlanningApplication?> GetApplicationAsync(
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.PlanningApplications
            .AsNoTracking()
            .Include(application => application.PlanningAuthority)
            .Include(application => application.PlanningDocuments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(application => application.Id == applicationId, cancellationToken);
    }
}
