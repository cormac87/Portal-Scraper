namespace PortalScraper.Services.Export;

public interface IPlanningApplicationExcelExportService
{
    IReadOnlyList<PlanningApplicationExportColumn> GetAvailableColumns();

    Task<PlanningApplicationExcelExportResult> ExportSearchResultsAsync(
        PlanningApplicationExcelExportRequest request,
        CancellationToken cancellationToken = default);
}
