namespace PortalScraper.Services.Geocoding;

public interface IPostcodeGeocodingService
{
    Task<Coordinate> GeocodePostcodeAsync(string postcode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PostcodeGeocodingResult>> GeocodePostcodesAsync(
        IReadOnlyList<string> postcodes,
        CancellationToken cancellationToken = default);
}
