namespace PortalScraper.Services.Geocoding;

public sealed record Coordinate(double Latitude, double Longitude);

public sealed record PostcodeGeocodingResult(
    string Query,
    Coordinate? Coordinate);
