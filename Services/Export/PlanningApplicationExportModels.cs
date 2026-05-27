namespace PortalScraper.Services.Export;

public sealed record PlanningApplicationExportColumn(string Key, string Header);

public sealed record PlanningApplicationExportCustomColumn(string Header, string Value);

public sealed record PlanningApplicationExportSelection(
    IReadOnlyList<string> ColumnKeys,
    IReadOnlyList<PlanningApplicationExportCustomColumn> CustomColumns);

public sealed record PlanningApplicationExcelExportRequest(
    IReadOnlyList<string> SearchConditions,
    IReadOnlyList<string> ColumnKeys,
    IReadOnlyList<PlanningApplicationExportCustomColumn> CustomColumns,
    IReadOnlyList<Guid>? PlanningAuthorityIds = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null);

public sealed record PlanningApplicationExcelExportResult(
    string FileName,
    string ContentType,
    byte[] Content);
