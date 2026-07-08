namespace PortalScraper.Services.Planning;

public sealed record PlanningLlmCompanyExtractionResult(
    IReadOnlyList<PlanningLlmCompanySuggestion> Suggestions,
    PlanningCompanyHouseNameMatchResult ConfirmedCompanies,
    int ProcessedDocumentCount,
    int SkippedDocumentCount,
    int ChunkCount,
    string Model);

public sealed record PlanningLlmCompanySuggestion(
    string Name,
    int MentionCount,
    int DocumentCount,
    IReadOnlyList<PlanningLlmCompanySuggestionDocument> Documents);

public sealed record PlanningLlmCompanySuggestionDocument(
    Guid DocumentId,
    string DocumentName,
    int MentionCount);
