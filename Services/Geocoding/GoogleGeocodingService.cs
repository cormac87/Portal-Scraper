using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PortalScraper.Services.Geocoding;

public sealed class GoogleGeocodingService(
    HttpClient httpClient,
    IOptions<GoogleMapsGeocodingOptions> options) : IGoogleGeocodingService
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.GeocodingApiKey);

    public async Task<Coordinate?> GeocodeAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var apiKey = options.Value.GeocodingApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Add a Google Maps geocoding API key to appsettings.json before refreshing authority locations.");
        }

        var path = $"maps/api/geocode/json?address={Uri.EscapeDataString(address)}&key={Uri.EscapeDataString(apiKey)}";
        var response = await httpClient.GetFromJsonAsync<GoogleGeocodingResponse>(path, cancellationToken);
        if (response is null)
        {
            return null;
        }

        if (!string.Equals(response.Status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(response.Status, "ZERO_RESULTS", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            throw new InvalidOperationException(response.ErrorMessage ?? $"Google Maps geocoding returned {response.Status}.");
        }

        var location = response.Results.FirstOrDefault()?.Geometry?.Location;
        return location is null
            ? null
            : new Coordinate(location.Latitude, location.Longitude);
    }

    private sealed class GoogleGeocodingResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("results")]
        public List<GoogleGeocodingResult> Results { get; set; } = [];
    }

    private sealed class GoogleGeocodingResult
    {
        [JsonPropertyName("geometry")]
        public GoogleGeocodingGeometry? Geometry { get; set; }
    }

    private sealed class GoogleGeocodingGeometry
    {
        [JsonPropertyName("location")]
        public GoogleGeocodingLocation? Location { get; set; }
    }

    private sealed class GoogleGeocodingLocation
    {
        [JsonPropertyName("lat")]
        public double Latitude { get; set; }

        [JsonPropertyName("lng")]
        public double Longitude { get; set; }
    }
}
