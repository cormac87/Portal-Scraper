namespace PortalScraper.Services.Planning;

public sealed record PlanningCompanyHouseNameMatchResult(
    IReadOnlyList<PlanningCompanyHouseNameMatch> Companies,
    int ProcessedDocumentCount,
    int SkippedDocumentCount,
    int CandidateNameCount,
    int TotalMentionCount);

public sealed record PlanningCompanyHouseNameMatch(
    Guid CompanyId,
    string CompanyName,
    string CompanyNumber,
    string? CompanyStatus,
    int MentionCount,
    int DocumentCount,
    IReadOnlyList<PlanningCompanyHouseNameDocumentMatch> Documents);

public sealed record PlanningCompanyHouseNameDocumentMatch(
    Guid DocumentId,
    string DocumentName,
    int MentionCount);

public sealed record PlanningCompanyHouseNameCandidate(
    Guid DocumentId,
    string DocumentName,
    string Name,
    int MentionCount);
