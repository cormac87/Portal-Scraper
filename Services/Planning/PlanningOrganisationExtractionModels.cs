namespace PortalScraper.Services.Planning;

public sealed record PlanningOrganisationExtractionResult(
    IReadOnlyList<PlanningOrganisationMatch> Organisations,
    int ProcessedDocumentCount,
    int SkippedDocumentCount,
    int TotalMentionCount);

public sealed record PlanningOrganisationMatch(
    string Name,
    int MentionCount,
    int DocumentCount,
    IReadOnlyList<PlanningOrganisationDocumentMatch> Documents);

public sealed record PlanningOrganisationDocumentMatch(
    Guid DocumentId,
    string DocumentName,
    int MentionCount);
