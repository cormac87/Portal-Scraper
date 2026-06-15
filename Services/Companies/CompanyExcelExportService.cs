using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

public sealed class CompanyExcelExportService(
    IDbContextFactory<ApplicationDbContext> dbFactory) : ICompanyExcelExportService
{
    private const int ExcelCellMaxLength = 32767;
    private const int ExcelMaxDataRows = 1048575;
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly CompanyExportColumnDefinition[] AvailableColumnDefinitions =
    [
        new("CompanyName", "Company name", company => company.CompanyName),
        new("CompanyNumber", "Company number", company => company.CompanyNumber),
        new("Email", "Email", company => company.Email),
        new("PhoneNumber", "Phone number", company => company.PhoneNumber),
        new("RegAddressCareOf", "Care of", company => company.RegAddressCareOf),
        new("RegAddressPoBox", "PO box", company => company.RegAddressPoBox),
        new("RegAddressAddressLine1", "Address line 1", company => company.RegAddressAddressLine1),
        new("RegAddressAddressLine2", "Address line 2", company => company.RegAddressAddressLine2),
        new("RegAddressPostTown", "Post town", company => company.RegAddressPostTown),
        new("RegAddressCounty", "County", company => company.RegAddressCounty),
        new("RegAddressCountry", "Country", company => company.RegAddressCountry),
        new("RegAddressPostCode", "Postcode", company => company.RegAddressPostCode),
        new("CompanyCategory", "Company category", company => company.CompanyCategory),
        new("CompanyStatus", "Company status", company => company.CompanyStatus),
        new("CountryOfOrigin", "Country of origin", company => company.CountryOfOrigin),
        new("DissolutionDate", "Dissolution date", company => company.DissolutionDate),
        new("IncorporationDate", "Incorporation date", company => company.IncorporationDate),
        new("AccountsAccountRefDay", "Accounts ref day", company => company.AccountsAccountRefDay),
        new("AccountsAccountRefMonth", "Accounts ref month", company => company.AccountsAccountRefMonth),
        new("AccountsNextDueDate", "Accounts next due date", company => company.AccountsNextDueDate),
        new("AccountsLastMadeUpDate", "Accounts last made up date", company => company.AccountsLastMadeUpDate),
        new("AccountsAccountCategory", "Accounts category", company => company.AccountsAccountCategory),
        new("ReturnsNextDueDate", "Returns next due date", company => company.ReturnsNextDueDate),
        new("ReturnsLastMadeUpDate", "Returns last made up date", company => company.ReturnsLastMadeUpDate),
        new("MortgagesNumMortCharges", "Mortgage charges", company => company.MortgagesNumMortCharges),
        new("MortgagesNumMortOutstanding", "Mortgage charges outstanding", company => company.MortgagesNumMortOutstanding),
        new("MortgagesNumMortPartSatisfied", "Mortgage charges part satisfied", company => company.MortgagesNumMortPartSatisfied),
        new("MortgagesNumMortSatisfied", "Mortgage charges satisfied", company => company.MortgagesNumMortSatisfied),
        new("SicCodeSicText1", "SIC code 1", company => company.SicCodeSicText1),
        new("SicCodeSicText2", "SIC code 2", company => company.SicCodeSicText2),
        new("SicCodeSicText3", "SIC code 3", company => company.SicCodeSicText3),
        new("SicCodeSicText4", "SIC code 4", company => company.SicCodeSicText4),
        new("LimitedPartnershipsNumGenPartners", "General partners", company => company.LimitedPartnershipsNumGenPartners),
        new("LimitedPartnershipsNumLimPartners", "Limited partners", company => company.LimitedPartnershipsNumLimPartners),
        new("Uri", "URI", company => company.Uri),
        new("PreviousName1ConDate", "Previous name 1 date", company => company.PreviousName1ConDate),
        new("PreviousName1CompanyName", "Previous name 1", company => company.PreviousName1CompanyName),
        new("PreviousName2ConDate", "Previous name 2 date", company => company.PreviousName2ConDate),
        new("PreviousName2CompanyName", "Previous name 2", company => company.PreviousName2CompanyName),
        new("PreviousName3ConDate", "Previous name 3 date", company => company.PreviousName3ConDate),
        new("PreviousName3CompanyName", "Previous name 3", company => company.PreviousName3CompanyName),
        new("PreviousName4ConDate", "Previous name 4 date", company => company.PreviousName4ConDate),
        new("PreviousName4CompanyName", "Previous name 4", company => company.PreviousName4CompanyName),
        new("PreviousName5ConDate", "Previous name 5 date", company => company.PreviousName5ConDate),
        new("PreviousName5CompanyName", "Previous name 5", company => company.PreviousName5CompanyName),
        new("PreviousName6ConDate", "Previous name 6 date", company => company.PreviousName6ConDate),
        new("PreviousName6CompanyName", "Previous name 6", company => company.PreviousName6CompanyName),
        new("PreviousName7ConDate", "Previous name 7 date", company => company.PreviousName7ConDate),
        new("PreviousName7CompanyName", "Previous name 7", company => company.PreviousName7CompanyName),
        new("PreviousName8ConDate", "Previous name 8 date", company => company.PreviousName8ConDate),
        new("PreviousName8CompanyName", "Previous name 8", company => company.PreviousName8CompanyName),
        new("PreviousName9ConDate", "Previous name 9 date", company => company.PreviousName9ConDate),
        new("PreviousName9CompanyName", "Previous name 9", company => company.PreviousName9CompanyName),
        new("PreviousName10ConDate", "Previous name 10 date", company => company.PreviousName10ConDate),
        new("PreviousName10CompanyName", "Previous name 10", company => company.PreviousName10CompanyName),
        new("ConfStmtNextDueDate", "Confirmation statement next due date", company => company.ConfStmtNextDueDate),
        new("ConfStmtLastMadeUpDate", "Confirmation statement last made up date", company => company.ConfStmtLastMadeUpDate),
        new("ImportedAtUtc", "Imported at UTC", company => company.ImportedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"))
    ];

    private static readonly IReadOnlyDictionary<string, CompanyExportColumnDefinition> ColumnDefinitionsByKey =
        AvailableColumnDefinitions.ToDictionary(column => column.Key, StringComparer.Ordinal);

    public IReadOnlyList<CompanyExportColumn> GetAvailableColumns()
    {
        return AvailableColumnDefinitions
            .Select(column => new CompanyExportColumn(column.Key, column.Header))
            .ToList();
    }

    public async Task<CompanyExcelExportResult> ExportSearchResultsAsync(
        CompanyExcelExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedColumns = GetSelectedColumns(request.ColumnKeys);
        var customColumns = request.CustomColumns
            .Where(column => !string.IsNullOrWhiteSpace(column.Header))
            .Select(column => new CompanyExportCustomColumn(column.Header.Trim(), column.Value))
            .ToList();

        if (selectedColumns.Count == 0 && customColumns.Count == 0)
        {
            throw new InvalidOperationException("Select at least one column to export.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = CompanyQuery.ApplyDefaultSort(CompanyQuery.ApplyFilters(
            db.Companies.AsNoTracking(),
            request.Filters));

        var totalCount = await query.CountAsync(cancellationToken);
        if (totalCount > ExcelMaxDataRows)
        {
            throw new InvalidOperationException($"Excel supports up to {ExcelMaxDataRows:N0} exported rows. Narrow the company search before exporting.");
        }

        var content = await BuildWorkbookAsync(query, selectedColumns, customColumns, cancellationToken);
        var fileName = $"companies-results-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";

        return new CompanyExcelExportResult(fileName, ExcelContentType, content);
    }

    private static List<CompanyExportColumnDefinition> GetSelectedColumns(IReadOnlyList<string> columnKeys)
    {
        return columnKeys
            .Where(key => ColumnDefinitionsByKey.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .Select(key => ColumnDefinitionsByKey[key])
            .ToList();
    }

    private static async Task<byte[]> BuildWorkbookAsync(
        IQueryable<Company> query,
        IReadOnlyList<CompanyExportColumnDefinition> selectedColumns,
        IReadOnlyList<CompanyExportCustomColumn> customColumns,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(worksheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());

                WriteRow(writer, selectedColumns
                    .Select(column => column.Header)
                    .Concat(customColumns.Select(column => column.Header)));

                await foreach (var company in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    WriteRow(writer, selectedColumns
                        .Select(column => column.ValueFactory(company))
                        .Concat(customColumns.Select(column => column.Value)));
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Companies"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static void WriteRow(OpenXmlWriter writer, IEnumerable<string?> values)
    {
        writer.WriteStartElement(new Row());

        foreach (var value in values)
        {
            writer.WriteElement(CreateTextCell(value));
        }

        writer.WriteEndElement();
    }

    private static Cell CreateTextCell(string? value)
    {
        var text = new Text(NormalizeCellText(value))
        {
            Space = SpaceProcessingModeValues.Preserve
        };

        return new Cell
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(text)
        };
    }

    private static string NormalizeCellText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (IsValidXmlTextCharacter(character))
            {
                normalized.Append(character);
            }

            if (normalized.Length >= ExcelCellMaxLength)
            {
                break;
            }
        }

        return normalized.ToString();
    }

    private static bool IsValidXmlTextCharacter(char character)
    {
        return character is '\t' or '\n' or '\r'
            || character is >= ' ' and <= '\uD7FF'
            || character is >= '\uE000' and <= '\uFFFD';
    }

    private sealed record CompanyExportColumnDefinition(
        string Key,
        string Header,
        Func<Company, string?> ValueFactory);
}
