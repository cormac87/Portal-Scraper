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

        var normalizedPageSize = Math.Max(1, pageSize);
        var currentPage = Math.Max(1, page);
        var authorities = await db.PlanningAuthorities
            .AsNoTracking()
            .OrderBy(authority => authority.Name)
            .ToListAsync(cancellationToken);

        var applicationRows = await db.PlanningApplications
            .AsNoTracking()
            .OrderByDescending(application => application.ScrapedAt)
            .ThenBy(application => application.ApplicationReference)
            .ThenBy(application => application.Id)
            .Skip(PlanningPagination.GetPageSkip(currentPage, normalizedPageSize))
            .Take(normalizedPageSize + 1)
            .Select(application => new
            {
                application.Id,
                application.ApplicationReference,
                application.Title,
                application.Description,
                PlanningAuthorityId = application.PlanningAuthority.Id,
                PlanningAuthorityName = application.PlanningAuthority.Name
            })
            .ToListAsync(cancellationToken);
        var hasNextPage = applicationRows.Count > normalizedPageSize;
        var applications = applicationRows
            .Take(normalizedPageSize)
            .Select(application => new PlanningApplication
            {
                Id = application.Id,
                ApplicationReference = application.ApplicationReference,
                Title = application.Title,
                Description = application.Description,
                PlanningAuthority = new PlanningAuthority
                {
                    Id = application.PlanningAuthorityId,
                    Name = application.PlanningAuthorityName
                }
            })
            .ToList();

        return new PlanningOverviewPage(
            authorities,
            applications,
            currentPage,
            ApplicationTotalCount: null,
            DocumentTotalCount: null,
            hasNextPage);
    }

    public async Task<PlanningOverviewCounts> GetOverviewCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var applicationTotalCount = await db.PlanningApplications.CountAsync(cancellationToken);
        var documentTotalCount = await db.PlanningDocuments.CountAsync(cancellationToken);

        return new PlanningOverviewCounts(applicationTotalCount, documentTotalCount);
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
