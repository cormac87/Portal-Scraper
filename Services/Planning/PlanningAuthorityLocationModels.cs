namespace PortalScraper.Services.Planning;

public sealed record PlanningAuthorityLocationMatch(
    Guid Id,
    string Name,
    string? Website,
    double Latitude,
    double Longitude,
    double DistanceKm);

public sealed record PlanningAuthorityLocationRefreshResult(
    IReadOnlyList<PlanningAuthorityLocationRefreshItem> Items,
    int UpdatedCount,
    int NotFoundCount,
    int FailedCount,
    string? ErrorMessage);

public sealed record PlanningAuthorityLocationRefreshItem(
    Guid AuthorityId,
    string AuthorityName,
    double? Latitude,
    double? Longitude,
    string Status,
    string? Message);
