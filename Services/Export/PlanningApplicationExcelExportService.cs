using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;

namespace PortalScraper.Services.Export;

public sealed class PlanningApplicationExcelExportService(
    IDbContextFactory<ApplicationDbContext> dbFactory) : IPlanningApplicationExcelExportService
{
    private const int ExcelCellMaxLength = 32767;
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string FullTextSearchAvailabilitySql = @"
SELECT CAST(
    CASE
        WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
            AND EXISTS (
                SELECT 1
                FROM sys.fulltext_indexes
                WHERE object_id = OBJECT_ID(N'dbo.PlanningApplication')
            )
            AND EXISTS (
                SELECT 1
                FROM sys.fulltext_indexes
                WHERE object_id = OBJECT_ID(N'dbo.PlanningDocument')
            )
        THEN 1
        ELSE 0
    END AS int) AS [Value]";

    private static readonly PlanningApplicationExportColumn[] AvailableColumns =
    [
        new("PlanningAuthorityName", "Planning authority"),
        new("ApplicationReference", "Application reference"),
        new("Title", "Title"),
        new("Description", "Description"),
        new("Status", "Status"),
        new("Address", "Address"),
        new("ReceivedDate", "Received date"),
        new("ValidatedDate", "Validated date"),
        new("ApplicantName", "Applicant name"),
        new("ApplicantEmail", "Applicant email"),
        new("ApplicantPhone", "Applicant phone"),
        new("AgentName", "Agent name"),
        new("CompanyName", "Agent company"),
        new("AgentEmail", "Agent email"),
        new("AgentPhone", "Agent phone"),
        new("SourceUrl", "Portal URL")
    ];

    private static readonly IReadOnlyDictionary<string, Func<PlanningApplicationExportRow, string?>> ColumnValueFactories =
        new Dictionary<string, Func<PlanningApplicationExportRow, string?>>(StringComparer.Ordinal)
        {
            ["PlanningAuthorityName"] = row => row.PlanningAuthorityName,
            ["ApplicationReference"] = row => row.ApplicationReference,
            ["Title"] = row => row.Title,
            ["Description"] = row => row.Description,
            ["Status"] = row => row.Status,
            ["Address"] = row => row.Address,
            ["ReceivedDate"] = row => FormatDate(row.ReceivedDate),
            ["ValidatedDate"] = row => FormatDate(row.ValidatedDate),
            ["ApplicantName"] = row => row.ApplicantName,
            ["ApplicantEmail"] = row => row.ApplicantEmail,
            ["ApplicantPhone"] = row => row.ApplicantPhone,
            ["AgentName"] = row => row.AgentName,
            ["CompanyName"] = row => row.CompanyName,
            ["AgentEmail"] = row => row.AgentEmail,
            ["AgentPhone"] = row => row.AgentPhone,
            ["SourceUrl"] = row => row.SourceUrl
        };

    public IReadOnlyList<PlanningApplicationExportColumn> GetAvailableColumns()
    {
        return AvailableColumns;
    }

    public async Task<PlanningApplicationExcelExportResult> ExportSearchResultsAsync(
        PlanningApplicationExcelExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorityFilterIds = NormalizeAuthorityFilter(request.PlanningAuthorityIds);
        if (request.SearchConditions.Count == 0 && authorityFilterIds is null)
        {
            throw new InvalidOperationException("Run a search before exporting planning results.");
        }

        var selectedColumns = GetSelectedColumns(request.ColumnKeys);
        var customColumns = request.CustomColumns
            .Where(column => !string.IsNullOrWhiteSpace(column.Header))
            .Select(column => new PlanningApplicationExportCustomColumn(column.Header.Trim(), column.Value))
            .ToList();

        if (selectedColumns.Count == 0 && customColumns.Count == 0)
        {
            throw new InvalidOperationException("Select at least one column to export.");
        }

        List<PlanningApplicationExportRow> rows;
        if (authorityFilterIds is { Count: 0 })
        {
            rows = [];
        }
        else
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (request.SearchConditions.Count > 0)
            {
                if (!await IsFullTextSearchAvailableAsync(db, cancellationToken))
                {
                    throw new InvalidOperationException("Keyword search export requires SQL Server Full-Text Search and the planning full-text indexes.");
                }

                rows = await ExecuteExportSearchAsync(db, request.SearchConditions, authorityFilterIds, cancellationToken);
            }
            else
            {
                rows = await ExecuteAuthorityFilteredExportAsync(db, authorityFilterIds!, cancellationToken);
            }
        }

        var content = BuildWorkbook(rows, selectedColumns, customColumns);
        var fileName = $"planning-results-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";

        return new PlanningApplicationExcelExportResult(fileName, ExcelContentType, content);
    }

    private static List<Guid>? NormalizeAuthorityFilter(IReadOnlyCollection<Guid>? planningAuthorityIds)
    {
        return planningAuthorityIds is null
            ? null
            : planningAuthorityIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
    }

    private static List<PlanningApplicationExportColumn> GetSelectedColumns(IReadOnlyList<string> columnKeys)
    {
        var selectedKeys = columnKeys
            .Where(key => ColumnValueFactories.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return AvailableColumns
            .Where(column => selectedKeys.Contains(column.Key))
            .ToList();
    }

    private static async Task<bool> IsFullTextSearchAvailableAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteScalarAsync(db, FullTextSearchAvailabilitySql, cancellationToken);

        return result is not null
            && result != DBNull.Value
            && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<object?> ExecuteScalarAsync(
        ApplicationDbContext db,
        string commandText,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State == ConnectionState.Closed;

        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;

            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static Task<List<PlanningApplicationExportRow>> ExecuteAuthorityFilteredExportAsync(
        ApplicationDbContext db,
        IReadOnlyList<Guid> authorityFilterIds,
        CancellationToken cancellationToken)
    {
        return db.PlanningApplications
            .AsNoTracking()
            .Include(application => application.PlanningAuthority)
            .Where(application => authorityFilterIds.Contains(application.PlanningAuthorityId))
            .OrderByDescending(application => application.ValidatedDate)
            .ThenByDescending(application => application.ReceivedDate)
            .ThenBy(application => application.ApplicationReference)
            .ThenBy(application => application.Title)
            .ThenBy(application => application.Id)
            .Select(application => new PlanningApplicationExportRow
            {
                PlanningAuthorityName = application.PlanningAuthority.Name,
                Title = application.Title,
                ReceivedDate = application.ReceivedDate,
                ValidatedDate = application.ValidatedDate,
                Status = application.Status,
                ApplicantEmail = application.ApplicantEmail,
                ApplicantPhone = application.ApplicantPhone,
                ApplicantName = application.ApplicantName,
                AgentEmail = application.AgentEmail,
                AgentPhone = application.AgentPhone,
                AgentName = application.AgentName,
                CompanyName = application.CompanyName,
                Address = application.Address,
                Description = application.Description,
                ApplicationReference = application.ApplicationReference,
                SourceUrl = application.SourceUrl
            })
            .ToListAsync(cancellationToken);
    }

    private static async Task<List<PlanningApplicationExportRow>> ExecuteExportSearchAsync(
        ApplicationDbContext db,
        IReadOnlyList<string> searchConditions,
        IReadOnlyList<Guid>? authorityFilterIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<PlanningApplicationExportRow>();
        var connection = db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State == ConnectionState.Closed;

        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BuildExportSearchSql(searchConditions.Count, authorityFilterIds);
            AddSearchConditionParameters(command, searchConditions);
            AddAuthorityFilterParameters(command, authorityFilterIds);
            AddParameter(command, "@SearchCriteriaCount", searchConditions.Count);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PlanningApplicationExportRow
                {
                    PlanningAuthorityName = reader.GetString(reader.GetOrdinal("PlanningAuthorityName")),
                    Title = reader.GetString(reader.GetOrdinal("Title")),
                    ReceivedDate = GetNullableDateTime(reader, "ReceivedDate"),
                    ValidatedDate = GetNullableDateTime(reader, "ValidatedDate"),
                    Status = GetNullableString(reader, "Status"),
                    ApplicantEmail = GetNullableString(reader, "ApplicantEmail"),
                    ApplicantPhone = GetNullableString(reader, "ApplicantPhone"),
                    ApplicantName = GetNullableString(reader, "ApplicantName"),
                    AgentEmail = GetNullableString(reader, "AgentEmail"),
                    AgentPhone = GetNullableString(reader, "AgentPhone"),
                    AgentName = GetNullableString(reader, "AgentName"),
                    CompanyName = GetNullableString(reader, "CompanyName"),
                    Address = GetNullableString(reader, "Address"),
                    Description = GetNullableString(reader, "Description"),
                    ApplicationReference = GetNullableString(reader, "ApplicationReference"),
                    SourceUrl = GetNullableString(reader, "SourceUrl")
                });
            }
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }

        return rows;
    }

    private static string BuildExportSearchSql(int searchCriteriaCount, IReadOnlyList<Guid>? authorityFilterIds)
    {
        var sql = new StringBuilder();
        AppendMatchedApplicationsCte(sql, searchCriteriaCount);
        sql.AppendLine(@",
RankedApplications AS
(
    SELECT
        [PlanningApplicationId],
        MAX([SearchRank]) AS [SearchRank]
    FROM [MatchedApplications]
    GROUP BY [PlanningApplicationId]
    HAVING COUNT(DISTINCT [CriterionIndex]) = @SearchCriteriaCount
)
SELECT
    [authority].[Name] AS [PlanningAuthorityName],
    [application].[Title],
    [application].[ReceivedDate],
    [application].[ValidatedDate],
    [application].[Status],
    [application].[ApplicantEmail],
    [application].[ApplicantPhone],
    [application].[ApplicantName],
    [application].[AgentEmail],
    [application].[AgentPhone],
    [application].[AgentName],
    [application].[CompanyName],
    [application].[Address],
    [application].[Description],
    [application].[ApplicationReference],
    [application].[SourceUrl]
FROM [RankedApplications]
    INNER JOIN [dbo].[PlanningApplication] AS [application]
        ON [application].[Id] = [RankedApplications].[PlanningApplicationId]
    INNER JOIN [dbo].[PlanningAuthority] AS [authority]
        ON [authority].[Id] = [application].[PlanningAuthorityId]
");
        AppendAuthorityFilterWhereClause(sql, authorityFilterIds);
        sql.AppendLine(@"ORDER BY [RankedApplications].[SearchRank] DESC,
    [application].[ValidatedDate] DESC,
    [application].[ReceivedDate] DESC,
    [application].[ApplicationReference],
    [application].[Title],
    [application].[Id]");

        return sql.ToString();
    }

    private static void AppendMatchedApplicationsCte(StringBuilder sql, int searchCriteriaCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(searchCriteriaCount, 1);

        sql.AppendLine("WITH MatchedApplications AS");
        sql.AppendLine("(");

        for (var index = 0; index < searchCriteriaCount; index++)
        {
            if (index > 0)
            {
                sql.AppendLine();
                sql.AppendLine("    UNION ALL");
                sql.AppendLine();
            }

            sql.AppendLine($@"    SELECT
        {index} AS [CriterionIndex],
        [matches].[RANK] AS [SearchRank],
        [document].[PlanningApplicationId]
    FROM CONTAINSTABLE([dbo].[PlanningDocument], ([Name], [DocumentType], [ContentText]), @SearchCondition{index}) AS [matches]
    INNER JOIN [dbo].[PlanningDocument] AS [document]
        ON [document].[Id] = [matches].[KEY]");

            sql.AppendLine();
            sql.AppendLine("    UNION ALL");
            sql.AppendLine();

            sql.AppendLine($@"    SELECT
        {index} AS [CriterionIndex],
        [matches].[RANK] AS [SearchRank],
        [matches].[KEY] AS [PlanningApplicationId]
    FROM CONTAINSTABLE([dbo].[PlanningApplication], ([Title], [Description], [Address]), @SearchCondition{index}) AS [matches]");
        }

        sql.AppendLine(")");
    }

    private static byte[] BuildWorkbook(
        IReadOnlyList<PlanningApplicationExportRow> rows,
        IReadOnlyList<PlanningApplicationExportColumn> selectedColumns,
        IReadOnlyList<PlanningApplicationExportCustomColumn> customColumns)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            sheetData.AppendChild(BuildHeaderRow(selectedColumns, customColumns));

            foreach (var row in rows)
            {
                sheetData.AppendChild(BuildDataRow(row, selectedColumns, customColumns));
            }

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Planning Results"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Row BuildHeaderRow(
        IReadOnlyList<PlanningApplicationExportColumn> selectedColumns,
        IReadOnlyList<PlanningApplicationExportCustomColumn> customColumns)
    {
        var row = new Row();

        foreach (var column in selectedColumns)
        {
            row.Append(CreateTextCell(column.Header));
        }

        foreach (var column in customColumns)
        {
            row.Append(CreateTextCell(column.Header));
        }

        return row;
    }

    private static Row BuildDataRow(
        PlanningApplicationExportRow exportRow,
        IReadOnlyList<PlanningApplicationExportColumn> selectedColumns,
        IReadOnlyList<PlanningApplicationExportCustomColumn> customColumns)
    {
        var row = new Row();

        foreach (var column in selectedColumns)
        {
            row.Append(CreateTextCell(ColumnValueFactories[column.Key](exportRow)));
        }

        foreach (var column in customColumns)
        {
            row.Append(CreateTextCell(column.Value));
        }

        return row;
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddSearchConditionParameters(DbCommand command, IReadOnlyList<string> searchConditions)
    {
        for (var index = 0; index < searchConditions.Count; index++)
        {
            AddParameter(command, $"@SearchCondition{index}", searchConditions[index]);
        }
    }

    private static void AddAuthorityFilterParameters(DbCommand command, IReadOnlyList<Guid>? authorityFilterIds)
    {
        if (authorityFilterIds is null)
        {
            return;
        }

        for (var index = 0; index < authorityFilterIds.Count; index++)
        {
            AddParameter(command, $"@AuthorityId{index}", authorityFilterIds[index]);
        }
    }

    private static void AppendAuthorityFilterWhereClause(StringBuilder sql, IReadOnlyList<Guid>? authorityFilterIds)
    {
        if (authorityFilterIds is null)
        {
            return;
        }

        var parameterNames = Enumerable
            .Range(0, authorityFilterIds.Count)
            .Select(index => $"@AuthorityId{index}");

        sql.AppendLine($"WHERE [application].[PlanningAuthorityId] IN ({string.Join(", ", parameterNames)})");
    }

    private static string? GetNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? GetNullableDateTime(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    private static string? FormatDate(DateTime? value)
    {
        return value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private sealed class PlanningApplicationExportRow
    {
        public string PlanningAuthorityName { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public DateTime? ReceivedDate { get; set; }

        public DateTime? ValidatedDate { get; set; }

        public string? Status { get; set; }

        public string? ApplicantEmail { get; set; }

        public string? ApplicantPhone { get; set; }

        public string? ApplicantName { get; set; }

        public string? AgentEmail { get; set; }

        public string? AgentPhone { get; set; }

        public string? AgentName { get; set; }

        public string? CompanyName { get; set; }

        public string? Address { get; set; }

        public string? Description { get; set; }

        public string? ApplicationReference { get; set; }

        public string? SourceUrl { get; set; }
    }
}
