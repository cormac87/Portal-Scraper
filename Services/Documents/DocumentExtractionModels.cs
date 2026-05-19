namespace PortalScraper.Services.Documents;

public sealed record DownloadedPlanningDocument(
    string Url,
    string FileName,
    string? ContentType,
    byte[] Content);

public sealed record PlanningDocumentTextExtractionResult(
    string? ContentText,
    string? FileName,
    string? ContentType,
    string ParseStatus,
    string? ParseError);
