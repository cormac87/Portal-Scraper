namespace PortalScraper.Services.Companies;

public interface ICompanyExcelExportService
{
    IReadOnlyList<CompanyExportColumn> GetAvailableColumns();

    Task<CompanyExcelExportResult> ExportSearchResultsAsync(
        CompanyExcelExportRequest request,
        CancellationToken cancellationToken = default);
}
