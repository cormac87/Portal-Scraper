namespace PortalScraper.Services.Geocoding;

public interface IGoogleGeocodingService
{
    bool IsConfigured { get; }

    Task<Coordinate?> GeocodeAsync(string address, CancellationToken cancellationToken = default);
}
