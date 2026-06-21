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
            throw new InvalidOperationException("Enter a postcode before finding nearby records.");
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

    public async Task<IReadOnlyList<PostcodeGeocodingResult>> GeocodePostcodesAsync(
        IReadOnlyList<string> postcodes,
        CancellationToken cancellationToken = default)
    {
        var normalizedPostcodes = postcodes
            .Where(postcode => !string.IsNullOrWhiteSpace(postcode))
            .Select(postcode => postcode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPostcodes.Count == 0)
        {
            return [];
        }

        if (normalizedPostcodes.Count > 100)
        {
            throw new InvalidOperationException("Postcodes.io accepts up to 100 postcodes in one bulk lookup.");
        }

        using var response = await httpClient.PostAsJsonAsync(
            "postcodes",
            new BulkPostcodesIoRequest(normalizedPostcodes),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<BulkPostcodesIoResponse>(cancellationToken);
        return payload?.Result?
            .Select(item => new PostcodeGeocodingResult(
                item.Query ?? string.Empty,
                item.Result?.Latitude is null || item.Result.Longitude is null
                    ? null
                    : new Coordinate(item.Result.Latitude.Value, item.Result.Longitude.Value)))
            .ToList() ?? [];
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

    private sealed record BulkPostcodesIoRequest(
        [property: JsonPropertyName("postcodes")] IReadOnlyList<string> Postcodes);

    private sealed class BulkPostcodesIoResponse
    {
        [JsonPropertyName("result")]
        public List<BulkPostcodesIoResult>? Result { get; set; }
    }

    private sealed class BulkPostcodesIoResult
    {
        [JsonPropertyName("query")]
        public string? Query { get; set; }

        [JsonPropertyName("result")]
        public PostcodesIoResult? Result { get; set; }
    }
}
