using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

public sealed record CompanySearchFilters(
    string? CompanyName = null,
    string? SicCode = null)
{
    public static CompanySearchFilters Empty { get; } = new();

    public bool HasActive => !string.IsNullOrWhiteSpace(CompanyName)
        || !string.IsNullOrWhiteSpace(SicCode);
}

public sealed record CompanySearchPage(
    IReadOnlyList<Company> Companies,
    int? TotalCount,
    int CurrentPage,
    bool HasNextPage);

public sealed record CompanySicCodeOption(
    string Code,
    string Description)
{
    public string Label => string.IsNullOrWhiteSpace(Description)
        ? Code
        : $"{Code} - {Description}";
}

public sealed record CompanyImportResult(
    string SourceFileName,
    int TotalRows,
    int InsertedRows,
    int UpdatedRows,
    int SkippedRows)
{
    public int ImportedRows => InsertedRows + UpdatedRows;
}

public sealed record CompanyContactScrapeResult(
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    int CandidateCompanies,
    int SearchedCompanies,
    int SkippedCompanies,
    int EmailsFound,
    int EmailsUpdated,
    int FailedCompanies,
    IReadOnlyList<string> Messages);
