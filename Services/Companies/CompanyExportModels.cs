namespace PortalScraper.Services.Companies;

public sealed record CompanyExportColumn(string Key, string Header);

public sealed record CompanyExportCustomColumn(string Header, string Value);

public sealed record CompanyExportSelection(
    IReadOnlyList<string> ColumnKeys,
    IReadOnlyList<CompanyExportCustomColumn> CustomColumns);

public sealed record CompanyExcelExportRequest(
    CompanySearchFilters Filters,
    IReadOnlyList<string> ColumnKeys,
    IReadOnlyList<CompanyExportCustomColumn> CustomColumns);

public sealed record CompanyExcelExportResult(
    string FileName,
    string ContentType,
    byte[] Content);
