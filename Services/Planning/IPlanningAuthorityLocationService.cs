namespace PortalScraper.Services.Planning;

public interface IPlanningAuthorityLocationService
{
    Task<PlanningAuthorityLocationRefreshResult> RefreshAuthorityLocationsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlanningAuthorityLocationMatch>> FindAuthoritiesWithinRadiusAsync(
        double latitude,
        double longitude,
        double radiusKm,
        CancellationToken cancellationToken = default);
}
