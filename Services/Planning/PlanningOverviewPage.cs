using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed record PlanningOverviewPage(
    IReadOnlyList<PlanningAuthority> Authorities,
    IReadOnlyList<PlanningApplication> Applications,
    int CurrentPage,
    int ApplicationTotalCount,
    int DocumentTotalCount);
