using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PortalScraper.Services.Geocoding;

public sealed class PostcodesIoGeocodingService(HttpClient httpClient) : IPostcodeGeocodingService
{
    public async Task<Coordinate> GeocodePostcodeAsync(
        string postcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            throw new InvalidOperationException("Enter a postcode before finding nearby authorities.");
        }

        var path = $"postcodes/{Uri.EscapeDataString(postcode.Trim())}";
        using var response = await httpClient.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("That postcode was not found.");
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<PostcodesIoResponse>(cancellationToken);
        var result = payload?.Result;
        if (result?.Latitude is null || result.Longitude is null)
        {
            throw new InvalidOperationException("The postcode lookup did not return coordinates.");
        }

        return new Coordinate(result.Latitude.Value, result.Longitude.Value);
    }

    private sealed class PostcodesIoResponse
    {
        [JsonPropertyName("result")]
        public PostcodesIoResult? Result { get; set; }
    }

    private sealed class PostcodesIoResult
    {
        [JsonPropertyName("latitude")]
        public double? Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double? Longitude { get; set; }
    }
}
