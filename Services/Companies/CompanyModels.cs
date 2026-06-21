using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

public sealed record CompanySearchFilters(
    string? CompanyName = null,
    string? SicCode = null,
    CompanyLocationSearch? Location = null)
{
    public static CompanySearchFilters Empty { get; } = new();

    public bool HasActive => !string.IsNullOrWhiteSpace(CompanyName)
        || !string.IsNullOrWhiteSpace(SicCode)
        || Location is not null;
}

public sealed record CompanyLocationSearch(
    string Postcode,
    double Latitude,
    double Longitude,
    double RadiusKm);

public sealed class CompanyLocationFilterInput
{
    public string Postcode { get; set; } = string.Empty;

    public double RadiusKm { get; set; } = 10;
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

public sealed record CompanyLocationCacheStats(
    long TotalCompanies,
    long CompaniesWithLocation,
    long CompaniesMissingLocationWithPostcode,
    int DistinctPostcodesPendingLookup);

public sealed record CompanyLocationRefreshResult(
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    int RequestedPostcodes,
    int AttemptedPostcodes,
    int UpdatedPostcodes,
    int NotFoundPostcodes,
    int FailedPostcodes,
    int UpdatedCompanies,
    int NotFoundCompanies,
    IReadOnlyList<CompanyLocationRefreshItem> Items,
    CompanyLocationCacheStats Stats);

public sealed record CompanyLocationRefreshItem(
    string Postcode,
    long CompanyCount,
    string Status,
    double? Latitude,
    double? Longitude,
    string? Message);
