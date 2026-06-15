namespace PortalScraper.Services.Companies;

public interface ICompanyImportService
{
    Task<CompanyImportResult> ImportAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default);
}
