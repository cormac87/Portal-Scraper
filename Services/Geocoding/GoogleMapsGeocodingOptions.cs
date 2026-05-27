namespace PortalScraper.Services.Geocoding;

public sealed class GoogleMapsGeocodingOptions
{
    public const string SectionName = "GoogleMaps";

    public string GeocodingApiKey { get; set; } = string.Empty;
}
