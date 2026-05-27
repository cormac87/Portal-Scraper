using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;
using PortalScraper.Services.Geocoding;

namespace PortalScraper.Services.Planning;

public sealed class PlanningAuthorityLocationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IGoogleGeocodingService geocodingService) : IPlanningAuthorityLocationService
{
    public async Task<PlanningAuthorityLocationRefreshResult> RefreshAuthorityLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!geocodingService.IsConfigured)
        {
            return new PlanningAuthorityLocationRefreshResult(
                [],
                UpdatedCount: 0,
                NotFoundCount: 0,
                FailedCount: 0,
                ErrorMessage: "Add a Google Maps geocoding API key to appsettings.json before refreshing authority locations.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var authorities = await db.PlanningAuthorities
            .OrderBy(authority => authority.Name)
            .ToListAsync(cancellationToken);

        var items = new List<PlanningAuthorityLocationRefreshItem>();
        var updatedCount = 0;
        var notFoundCount = 0;
        var failedCount = 0;

        foreach (var authority in authorities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var coordinate = await geocodingService.GeocodeAsync(
                    $"{authority.Name} planning authority, United Kingdom",
                    cancellationToken);

                if (coordinate is null)
                {
                    notFoundCount++;
                    items.Add(new PlanningAuthorityLocationRefreshItem(
                        authority.Id,
                        authority.Name,
                        authority.Latitude,
                        authority.Longitude,
                        "Not found",
                        "Google Maps returned no geocode result."));
                    continue;
                }

                authority.Latitude = coordinate.Latitude;
                authority.Longitude = coordinate.Longitude;
                updatedCount++;

                items.Add(new PlanningAuthorityLocationRefreshItem(
                    authority.Id,
                    authority.Name,
                    coordinate.Latitude,
                    coordinate.Longitude,
                    "Updated",
                    null));
            }
            catch (Exception ex)
            {
                failedCount++;
                items.Add(new PlanningAuthorityLocationRefreshItem(
                    authority.Id,
                    authority.Name,
                    authority.Latitude,
                    authority.Longitude,
                    "Failed",
                    ex.Message));
            }
        }

        if (updatedCount > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new PlanningAuthorityLocationRefreshResult(
            items,
            updatedCount,
            notFoundCount,
            failedCount,
            ErrorMessage: null);
    }

    public async Task<IReadOnlyList<PlanningAuthorityLocationMatch>> FindAuthoritiesWithinRadiusAsync(
        double latitude,
        double longitude,
        double radiusKm,
        CancellationToken cancellationToken = default)
    {
        if (radiusKm <= 0)
        {
            throw new InvalidOperationException("Enter a distance greater than 0 km.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var authorities = await db.PlanningAuthorities
            .AsNoTracking()
            .Where(authority => authority.Latitude != null && authority.Longitude != null)
            .OrderBy(authority => authority.Name)
            .Select(authority => new
            {
                authority.Id,
                authority.Name,
                authority.Website,
                Latitude = authority.Latitude!.Value,
                Longitude = authority.Longitude!.Value
            })
            .ToListAsync(cancellationToken);

        return authorities
            .Select(authority => new PlanningAuthorityLocationMatch(
                authority.Id,
                authority.Name,
                authority.Website,
                authority.Latitude,
                authority.Longitude,
                CalculateDistanceKm(latitude, longitude, authority.Latitude, authority.Longitude)))
            .Where(authority => authority.DistanceKm <= radiusKm)
            .OrderBy(authority => authority.DistanceKm)
            .ThenBy(authority => authority.Name)
            .ToList();
    }

    private static double CalculateDistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double EarthRadiusKm = 6371.0088;
        var latitudeRadians1 = ToRadians(latitude1);
        var latitudeRadians2 = ToRadians(latitude2);
        var latitudeDelta = ToRadians(latitude2 - latitude1);
        var longitudeDelta = ToRadians(longitude2 - longitude1);

        var halfChordLength = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(latitudeRadians1) * Math.Cos(latitudeRadians2)
            * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);

        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(halfChordLength), Math.Sqrt(1 - halfChordLength));
    }

    private static double ToRadians(double degrees)
    {
        return degrees * Math.PI / 180;
    }
}
