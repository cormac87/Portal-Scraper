using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using ExcelDataReader;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

public sealed class CompanyImportService(
    IDbContextFactory<ApplicationDbContext> dbFactory) : ICompanyImportService
{
    private const int BatchSize = 5000;

    private static readonly string[] ImportPropertyNames =
    [
        nameof(Company.CompanyName),
        nameof(Company.CompanyNumber),
        nameof(Company.RegAddressCareOf),
        nameof(Company.RegAddressPoBox),
        nameof(Company.RegAddressAddressLine1),
        nameof(Company.RegAddressAddressLine2),
        nameof(Company.RegAddressPostTown),
        nameof(Company.RegAddressCounty),
        nameof(Company.RegAddressCountry),
        nameof(Company.RegAddressPostCode),
        nameof(Company.CompanyCategory),
        nameof(Company.CompanyStatus),
        nameof(Company.CountryOfOrigin),
        nameof(Company.DissolutionDate),
        nameof(Company.IncorporationDate),
        nameof(Company.AccountsAccountRefDay),
        nameof(Company.AccountsAccountRefMonth),
        nameof(Company.AccountsNextDueDate),
        nameof(Company.AccountsLastMadeUpDate),
        nameof(Company.AccountsAccountCategory),
        nameof(Company.ReturnsNextDueDate),
        nameof(Company.ReturnsLastMadeUpDate),
        nameof(Company.MortgagesNumMortCharges),
        nameof(Company.MortgagesNumMortOutstanding),
        nameof(Company.MortgagesNumMortPartSatisfied),
        nameof(Company.MortgagesNumMortSatisfied),
        nameof(Company.SicCodeSicText1),
        nameof(Company.SicCodeSicText2),
        nameof(Company.SicCodeSicText3),
        nameof(Company.SicCodeSicText4),
        nameof(Company.LimitedPartnershipsNumGenPartners),
        nameof(Company.LimitedPartnershipsNumLimPartners),
        nameof(Company.Uri),
        nameof(Company.PreviousName1ConDate),
        nameof(Company.PreviousName1CompanyName),
        nameof(Company.PreviousName2ConDate),
        nameof(Company.PreviousName2CompanyName),
        nameof(Company.PreviousName3ConDate),
        nameof(Company.PreviousName3CompanyName),
        nameof(Company.PreviousName4ConDate),
        nameof(Company.PreviousName4CompanyName),
        nameof(Company.PreviousName5ConDate),
        nameof(Company.PreviousName5CompanyName),
        nameof(Company.PreviousName6ConDate),
        nameof(Company.PreviousName6CompanyName),
        nameof(Company.PreviousName7ConDate),
        nameof(Company.PreviousName7CompanyName),
        nameof(Company.PreviousName8ConDate),
        nameof(Company.PreviousName8CompanyName),
        nameof(Company.PreviousName9ConDate),
        nameof(Company.PreviousName9CompanyName),
        nameof(Company.PreviousName10ConDate),
        nameof(Company.PreviousName10CompanyName),
        nameof(Company.ConfStmtNextDueDate),
        nameof(Company.ConfStmtLastMadeUpDate)
    ];

    private static readonly IReadOnlyList<CompanyImportColumn> ImportColumns = ImportPropertyNames
        .Select(CreateImportColumn)
        .ToList();

    private static readonly IReadOnlyDictionary<string, CompanyImportColumn> ImportColumnsByNormalizedHeader = ImportColumns
        .ToDictionary(column => NormalizeHeader(column.Name), StringComparer.Ordinal);

    private static readonly string CreateTempTableSql = BuildCreateTempTableSql();
    private static readonly string MergeSql = BuildMergeSql();

    public async Task<CompanyImportResult> ImportAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"portal-scraper-company-import-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");

        await using (var tempWriteStream = new FileStream(
            tempFilePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.CopyToAsync(tempWriteStream, cancellationToken);
        }

        var counters = new ImportCounters();
        var batch = new Dictionary<string, Company>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var tempReadStream = new FileStream(
                tempFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var connection = (SqlConnection)db.Database.GetDbConnection();

            await db.Database.OpenConnectionAsync(cancellationToken);
            await ExecuteNonQueryAsync(db, connection, CreateTempTableSql, cancellationToken);

            async Task AddCompanyAsync(Company company)
            {
                cancellationToken.ThrowIfCancellationRequested();

                counters.TotalRows++;
                var companyNumber = company.CompanyNumber.Trim();
                if (string.IsNullOrWhiteSpace(companyNumber))
                {
                    counters.SkippedRows++;
                    return;
                }

                company.CompanyNumber = companyNumber;
                batch[companyNumber] = company;

                if (batch.Count >= BatchSize)
                {
                    await FlushBatchAsync(db, connection, batch, counters, cancellationToken);
                }
            }

            var importedSourceFileName = await ImportSupportedStreamAsync(tempReadStream, fileName, AddCompanyAsync, cancellationToken);
            await FlushBatchAsync(db, connection, batch, counters, cancellationToken);

            return new CompanyImportResult(
                importedSourceFileName,
                counters.TotalRows,
                counters.InsertedRows,
                counters.UpdatedRows,
                counters.SkippedRows);
        }
        finally
        {
            File.Delete(tempFilePath);
        }
    }

    private static async Task<string> ImportSupportedStreamAsync(
        Stream stream,
        string fileName,
        Func<Company, Task> addCompanyAsync,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.Entries
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
                .FirstOrDefault(candidate => IsSupportedSourceFile(candidate.Name));

            if (entry is null)
            {
                throw new InvalidOperationException("The zip file did not contain a CSV or Excel company data file.");
            }

            await using var entryStream = entry.Open();
            return await ImportSupportedStreamAsync(entryStream, entry.Name, addCompanyAsync, cancellationToken);
        }

        if (IsCsvFile(fileName) || string.IsNullOrWhiteSpace(extension))
        {
            await ImportCsvAsync(stream, addCompanyAsync, cancellationToken);
            return fileName;
        }

        if (IsExcelFile(fileName))
        {
            await ImportWorkbookAsync(stream, addCompanyAsync, cancellationToken);
            return fileName;
        }

        throw new InvalidOperationException("Upload a CSV, ZIP, XLS, or XLSX company data file.");
    }

    private static async Task ImportCsvAsync(
        Stream stream,
        Func<Company, Task> addCompanyAsync,
        CancellationToken cancellationToken)
    {
        using var parser = new TextFieldParser(stream, Encoding.UTF8, detectEncoding: true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        var headers = parser.ReadFields();
        if (headers is null || headers.Length == 0)
        {
            throw new InvalidOperationException("The company data file does not contain a header row.");
        }

        var headerMap = BuildHeaderMap(headers);

        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fields = parser.ReadFields();
            if (fields is null || fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            await addCompanyAsync(MapCompany(headerMap, fields));
        }
    }

    private static async Task ImportWorkbookAsync(
        Stream stream,
        Func<Company, Task> addCompanyAsync,
        CancellationToken cancellationToken)
    {
        using var reader = ExcelReaderFactory.CreateReader(stream);
        string?[]? headers = null;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = ReadWorkbookRow(reader);
            if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                headers = row;
                break;
            }
        }

        if (headers is null || headers.Length == 0)
        {
            throw new InvalidOperationException("The company data workbook does not contain a header row.");
        }

        var headerMap = BuildHeaderMap(headers);

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fields = ReadWorkbookRow(reader);
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            await addCompanyAsync(MapCompany(headerMap, fields));
        }
    }

    private static string?[] ReadWorkbookRow(IExcelDataReader reader)
    {
        var fields = new string?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            fields[index] = ConvertWorkbookValue(reader.GetValue(index));
        }

        return fields;
    }

    private static CompanyImportColumn?[] BuildHeaderMap(IReadOnlyList<string?> headers)
    {
        var headerMap = new CompanyImportColumn?[headers.Count];
        var hasCompanyNumber = false;

        for (var index = 0; index < headers.Count; index++)
        {
            var normalizedHeader = NormalizeHeader(headers[index]);
            if (ImportColumnsByNormalizedHeader.TryGetValue(normalizedHeader, out var column))
            {
                headerMap[index] = column;
                hasCompanyNumber |= column.Name == nameof(Company.CompanyNumber);
            }
        }

        if (!hasCompanyNumber)
        {
            throw new InvalidOperationException("The company data file must include a CompanyNumber column.");
        }

        return headerMap;
    }

    private static Company MapCompany(
        IReadOnlyList<CompanyImportColumn?> headerMap,
        IReadOnlyList<string?> fields)
    {
        var company = new Company();

        for (var index = 0; index < headerMap.Count && index < fields.Count; index++)
        {
            var column = headerMap[index];
            if (column is null)
            {
                continue;
            }

            column.SetValue(company, fields[index]);
        }

        return company;
    }

    private static async Task FlushBatchAsync(
        ApplicationDbContext db,
        SqlConnection connection,
        Dictionary<string, Company> batch,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await ExecuteNonQueryAsync(db, connection, "TRUNCATE TABLE #CompanyImport;", cancellationToken);

        var table = BuildImportTable(batch.Values);
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            BatchSize = batch.Count,
            DestinationTableName = "#CompanyImport"
        };
        ApplyConfiguredBulkCopyTimeout(db, bulkCopy);

        foreach (var column in ImportColumns)
        {
            bulkCopy.ColumnMappings.Add(column.Name, column.Name);
        }

        bulkCopy.ColumnMappings.Add(nameof(Company.ImportedAtUtc), nameof(Company.ImportedAtUtc));
        await bulkCopy.WriteToServerAsync(table, cancellationToken);

        await using var command = connection.CreateCommand();
        ApplyConfiguredCommandTimeout(db, command);
        command.CommandText = MergeSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var action = reader.GetString(0);
            var count = reader.GetInt32(1);
            if (action.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                counters.InsertedRows += count;
            }
            else if (action.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                counters.UpdatedRows += count;
            }
        }

        batch.Clear();
    }

    private static DataTable BuildImportTable(IEnumerable<Company> companies)
    {
        var importedAtUtc = DateTime.UtcNow;
        var table = new DataTable
        {
            Locale = CultureInfo.InvariantCulture
        };

        foreach (var column in ImportColumns)
        {
            table.Columns.Add(column.Name, typeof(string));
        }

        table.Columns.Add(nameof(Company.ImportedAtUtc), typeof(DateTime));

        foreach (var company in companies)
        {
            var row = table.NewRow();
            foreach (var column in ImportColumns)
            {
                row[column.Name] = column.GetValue(company) ?? (object)DBNull.Value;
            }

            row[nameof(Company.ImportedAtUtc)] = importedAtUtc;
            table.Rows.Add(row);
        }

        return table;
    }

    private static async Task ExecuteNonQueryAsync(
        ApplicationDbContext db,
        SqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ApplyConfiguredCommandTimeout(db, command);
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ApplyConfiguredCommandTimeout(ApplicationDbContext db, SqlCommand command)
    {
        var commandTimeout = db.Database.GetCommandTimeout();
        if (commandTimeout.HasValue)
        {
            command.CommandTimeout = commandTimeout.Value;
        }
    }

    private static void ApplyConfiguredBulkCopyTimeout(ApplicationDbContext db, SqlBulkCopy bulkCopy)
    {
        var commandTimeout = db.Database.GetCommandTimeout();
        if (commandTimeout.HasValue)
        {
            bulkCopy.BulkCopyTimeout = commandTimeout.Value;
        }
    }

    private static bool IsSupportedSourceFile(string fileName)
    {
        return IsCsvFile(fileName) || IsExcelFile(fileName);
    }

    private static bool IsCsvFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcelFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ConvertWorkbookValue(object? value)
    {
        return value switch
        {
            null => null,
            DateTime date => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static string? NormalizeValue(string? value, int? maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (maxLength.HasValue && normalized.Length > maxLength.Value)
        {
            normalized = normalized[..maxLength.Value];
        }

        return normalized;
    }

    private static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(header.Length);
        foreach (var character in header)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static CompanyImportColumn CreateImportColumn(string propertyName)
    {
        var property = typeof(Company).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Company property '{propertyName}' was not found.");

        return new CompanyImportColumn(
            property,
            property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength,
            property.GetCustomAttribute<RequiredAttribute>() is not null);
    }

    private static string BuildCreateTempTableSql()
    {
        var columnDefinitions = ImportColumns
            .Select(column => $"    [{column.Name}] {column.SqlType} {(column.IsRequired ? "NOT NULL" : "NULL")}")
            .Append($"    [{nameof(Company.ImportedAtUtc)}] DATETIME2(7) NOT NULL");

        return $@"
IF OBJECT_ID('tempdb..#CompanyImport') IS NOT NULL
BEGIN
    DROP TABLE #CompanyImport;
END;

CREATE TABLE #CompanyImport
(
{string.Join($",{Environment.NewLine}", columnDefinitions)}
);";
    }

    private static string BuildMergeSql()
    {
        var updateColumns = ImportColumns
            .Where(column => column.Name != nameof(Company.CompanyNumber))
            .Select(column => $"    [target].[{column.Name}] = [source].[{column.Name}]")
            .Concat(GetLocationCacheResetUpdateColumns())
            .Append($"    [target].[{nameof(Company.ImportedAtUtc)}] = [source].[{nameof(Company.ImportedAtUtc)}]");

        var insertColumns = ImportColumns
            .Select(column => $"[{column.Name}]")
            .Append($"[{nameof(Company.ImportedAtUtc)}]")
            .ToList();

        var insertValues = ImportColumns
            .Select(column => $"[source].[{column.Name}]")
            .Append($"[source].[{nameof(Company.ImportedAtUtc)}]")
            .ToList();

        return $@"
DECLARE @MergeActions TABLE ([Action] NVARCHAR(10) NOT NULL);

MERGE [dbo].[Company] WITH (HOLDLOCK) AS [target]
USING #CompanyImport AS [source]
    ON [target].[CompanyNumber] = [source].[CompanyNumber]
WHEN MATCHED THEN
    UPDATE SET
{string.Join($",{Environment.NewLine}", updateColumns)}
WHEN NOT MATCHED BY TARGET THEN
    INSERT ({string.Join(", ", insertColumns)})
    VALUES ({string.Join(", ", insertValues)})
OUTPUT $action INTO @MergeActions;

SELECT [Action], COUNT(*) AS [ActionCount]
FROM @MergeActions
GROUP BY [Action];";
    }

    private static IEnumerable<string> GetLocationCacheResetUpdateColumns()
    {
        const string postcodeChangedExpression = "ISNULL(UPPER(REPLACE([target].[RegAddressPostCode], N' ', N'')), N'') <> ISNULL(UPPER(REPLACE([source].[RegAddressPostCode], N' ', N'')), N'')";

        yield return $"    [target].[{nameof(Company.Latitude)}] = CASE WHEN {postcodeChangedExpression} THEN NULL ELSE [target].[{nameof(Company.Latitude)}] END";
        yield return $"    [target].[{nameof(Company.Longitude)}] = CASE WHEN {postcodeChangedExpression} THEN NULL ELSE [target].[{nameof(Company.Longitude)}] END";
        yield return $"    [target].[{nameof(Company.Location)}] = CASE WHEN {postcodeChangedExpression} THEN NULL ELSE [target].[{nameof(Company.Location)}] END";
        yield return $"    [target].[{nameof(Company.LocationLookupStatus)}] = CASE WHEN {postcodeChangedExpression} THEN NULL ELSE [target].[{nameof(Company.LocationLookupStatus)}] END";
        yield return $"    [target].[{nameof(Company.LocationLookupMessage)}] = CASE WHEN {postcodeChangedExpression} THEN NULL ELSE [target].[{nameof(Company.LocationLookupMessage)}] END";
        yield return $"    [target].[{nameof(Company.LocationLookupAtUtc)}] = CASE WHEN {postcodeChangedExpression} THEN NULL ELSE [target].[{nameof(Company.LocationLookupAtUtc)}] END";
    }

    private sealed class CompanyImportColumn(
        PropertyInfo property,
        int? maxLength,
        bool isRequired)
    {
        public string Name { get; } = property.Name;

        public string SqlType { get; } = maxLength.HasValue
            ? $"NVARCHAR({maxLength.Value})"
            : "NVARCHAR(MAX)";

        public bool IsRequired { get; } = isRequired;

        public void SetValue(Company company, string? value)
        {
            property.SetValue(company, NormalizeValue(value, maxLength));
        }

        public string? GetValue(Company company)
        {
            return property.GetValue(company) as string;
        }
    }

    private sealed class ImportCounters
    {
        public int TotalRows { get; set; }

        public int InsertedRows { get; set; }

        public int UpdatedRows { get; set; }

        public int SkippedRows { get; set; }
    }
}
