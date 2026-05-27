namespace PortalScraper.Services.Geocoding;

public interface IPostcodeGeocodingService
{
    Task<Coordinate> GeocodePostcodeAsync(string postcode, CancellationToken cancellationToken = default);
}
