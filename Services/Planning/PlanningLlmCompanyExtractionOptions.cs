namespace PortalScraper.Services.Planning;

public sealed class PlanningLlmCompanyExtractionOptions
{
    public const string SectionName = "PlanningLlmCompanyExtraction";

    public string EndpointUrl { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "gemma3:1b";

    public int TimeoutSeconds { get; set; } = 180;

    public int MaxChunkCharacters { get; set; } = 6000;

    public int MaxChunksPerDocument { get; set; } = 8;

    public int MaxDocuments { get; set; } = 10;

    public int MaxSuggestedNamesPerChunk { get; set; } = 20;
}
