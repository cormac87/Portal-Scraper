using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class PlanningSearchService(IDbContextFactory<ApplicationDbContext> dbFactory) : IPlanningSearchService
{
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

    private static readonly Regex KeywordRegex = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> IgnoredSearchTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "by",
        "for",
        "from",
        "in",
        "into",
        "is",
        "it",
        "of",
        "on",
        "or",
        "that",
        "the",
        "to",
        "with"
    };

    public List<FullTextSearchCriterion> BuildCriteria(IEnumerable<PlanningSearchCriterionInput> criteria)
    {
        var searchCriteria = new List<FullTextSearchCriterion>();

        foreach (var criterion in criteria)
        {
            var displayText = criterion.Text.Trim();
            var searchTerms = ExtractSearchTerms(displayText);

            if (searchTerms.Count > 0)
            {
                searchCriteria.Add(new FullTextSearchCriterion
                {
                    DisplayText = displayText,
                    SearchCondition = BuildFullTextSearchCondition(searchTerms, criterion.RequireAdjacentWords),
                    SearchTerms = searchTerms,
                    RequireAdjacentWords = criterion.RequireAdjacentWords
                });
            }
        }

        return searchCriteria;
    }

    public string FormatCriteriaSummary(IReadOnlyList<FullTextSearchCriterion> criteria)
    {
        return string.Join(" + ", criteria.Select(criterion => criterion.DisplayText));
    }

    public async Task<PlanningKeywordSearchPage> SearchAsync(
        IReadOnlyList<FullTextSearchCriterion> criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (criteria.Count == 0)
        {
            return new PlanningKeywordSearchPage([], 0, 1, IsFullTextSearchAvailable: true);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsFullTextSearchAvailableAsync(db, cancellationToken))
        {
            return new PlanningKeywordSearchPage([], 0, 1, IsFullTextSearchAvailable: false);
        }

        var searchConditions = criteria
            .Select(criterion => criterion.SearchCondition)
            .ToList();
        var totalCount = await ExecuteSearchCountAsync(db, searchConditions, cancellationToken);
        var currentPage = PlanningPagination.ClampPage(page, totalCount, pageSize);
        var results = totalCount == 0
            ? []
            : await ExecuteSearchAsync(
                db,
                searchConditions,
                PlanningPagination.GetPageSkip(currentPage, pageSize),
                pageSize,
                cancellationToken);

        return new PlanningKeywordSearchPage(results, totalCount, currentPage, IsFullTextSearchAvailable: true);
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

    private static async Task<List<PlanningSearchResult>> ExecuteSearchAsync(
        ApplicationDbContext db,
        IReadOnlyList<string> searchConditions,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var results = new List<PlanningSearchResult>();
        var connection = db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State == ConnectionState.Closed;

        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BuildKeywordSearchSql(searchConditions.Count);
            AddSearchConditionParameters(command, searchConditions);
            AddParameter(command, "@SearchCriteriaCount", searchConditions.Count);
            AddParameter(command, "@Skip", skip);
            AddParameter(command, "@Take", take);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new PlanningSearchResult
                {
                    MatchType = reader.GetString(reader.GetOrdinal("MatchType")),
                    PlanningApplicationId = reader.GetGuid(reader.GetOrdinal("PlanningApplicationId")),
                    PlanningDocumentId = GetNullableGuid(reader, "PlanningDocumentId"),
                    PlanningAuthorityName = reader.GetString(reader.GetOrdinal("PlanningAuthorityName")),
                    ApplicationReference = GetNullableString(reader, "ApplicationReference"),
                    ApplicationTitle = reader.GetString(reader.GetOrdinal("ApplicationTitle")),
                    Status = GetNullableString(reader, "Status"),
                    Address = GetNullableString(reader, "Address"),
                    ReceivedDate = GetNullableDateTime(reader, "ReceivedDate"),
                    ValidatedDate = GetNullableDateTime(reader, "ValidatedDate"),
                    DocumentName = GetNullableString(reader, "DocumentName"),
                    DocumentType = GetNullableString(reader, "DocumentType"),
                    Url = GetNullableString(reader, "Url"),
                    PublishedDate = GetNullableDateTime(reader, "PublishedDate"),
                    SearchRank = reader.GetInt32(reader.GetOrdinal("SearchRank")),
                    PreviewText = GetNullableString(reader, "PreviewText")
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

        return results;
    }

    private static string BuildKeywordSearchSql(int searchCriteriaCount)
    {
        var sql = new StringBuilder();
        AppendMatchedApplicationsCte(sql, searchCriteriaCount, includeRankAndFlags: true);
        sql.AppendLine(@",
RankedApplications AS
(
    SELECT
        [PlanningApplicationId],
        MAX([SearchRank]) AS [SearchRank],
        MAX([HasApplicationMatch]) AS [HasApplicationMatch],
        MAX([HasDocumentMatch]) AS [HasDocumentMatch]
    FROM [MatchedApplications]
    GROUP BY [PlanningApplicationId]
    HAVING COUNT(DISTINCT [CriterionIndex]) = @SearchCriteriaCount
)
SELECT
    CAST(
        CASE
            WHEN [RankedApplications].[HasApplicationMatch] = 1 AND [RankedApplications].[HasDocumentMatch] = 1 THEN N'Application + Document'
            WHEN [RankedApplications].[HasDocumentMatch] = 1 THEN N'Document'
            ELSE N'Application'
        END AS nvarchar(30)) AS [MatchType],
    [application].[Id] AS [PlanningApplicationId],
    CAST(NULL AS uniqueidentifier) AS [PlanningDocumentId],
    [authority].[Name] AS [PlanningAuthorityName],
    [application].[ApplicationReference] AS [ApplicationReference],
    [application].[Title] AS [ApplicationTitle],
    [application].[Status] AS [Status],
    [application].[Address] AS [Address],
    [application].[ReceivedDate] AS [ReceivedDate],
    [application].[ValidatedDate] AS [ValidatedDate],
    CAST(NULL AS nvarchar(255)) AS [DocumentName],
    CAST(NULL AS nvarchar(50)) AS [DocumentType],
    [application].[SourceUrl] AS [Url],
    CAST(NULL AS datetime2(7)) AS [PublishedDate],
    [RankedApplications].[SearchRank],
    CAST(NULL AS nvarchar(max)) AS [PreviewText]
FROM [RankedApplications]
    INNER JOIN [dbo].[PlanningApplication] AS [application]
        ON [application].[Id] = [RankedApplications].[PlanningApplicationId]
    INNER JOIN [dbo].[PlanningAuthority] AS [authority]
        ON [authority].[Id] = [application].[PlanningAuthorityId]
ORDER BY [RankedApplications].[SearchRank] DESC,
    [application].[ValidatedDate] DESC,
    [application].[ReceivedDate] DESC,
    [application].[ApplicationReference],
    [application].[Title],
    [application].[Id]
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY");

        return sql.ToString();
    }

    private static string BuildKeywordSearchCountSql(int searchCriteriaCount)
    {
        var sql = new StringBuilder();
        AppendMatchedApplicationsCte(sql, searchCriteriaCount, includeRankAndFlags: false);
        sql.AppendLine(@",
RankedApplications AS
(
    SELECT [PlanningApplicationId]
    FROM [MatchedApplications]
    GROUP BY [PlanningApplicationId]
    HAVING COUNT(DISTINCT [CriterionIndex]) = @SearchCriteriaCount
)
SELECT CAST(COUNT_BIG(*) AS int) AS [Value]
FROM [RankedApplications]");

        return sql.ToString();
    }

    private static void AppendMatchedApplicationsCte(StringBuilder sql, int searchCriteriaCount, bool includeRankAndFlags)
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

            AppendMatchedDocumentSelect(sql, index, includeRankAndFlags);
            sql.AppendLine();
            sql.AppendLine("    UNION ALL");
            sql.AppendLine();
            AppendMatchedApplicationSelect(sql, index, includeRankAndFlags);
        }

        sql.AppendLine(")");
    }

    private static void AppendMatchedDocumentSelect(StringBuilder sql, int index, bool includeRankAndFlags)
    {
        if (includeRankAndFlags)
        {
            sql.AppendLine($@"    SELECT
        {index} AS [CriterionIndex],
        [matches].[RANK] AS [SearchRank],
        [document].[PlanningApplicationId],
        CAST(0 AS int) AS [HasApplicationMatch],
        CAST(1 AS int) AS [HasDocumentMatch]
    FROM CONTAINSTABLE([dbo].[PlanningDocument], ([Name], [DocumentType], [ContentText]), @SearchCondition{index}) AS [matches]
    INNER JOIN [dbo].[PlanningDocument] AS [document]
        ON [document].[Id] = [matches].[KEY]");
            return;
        }

        sql.AppendLine($@"    SELECT
        {index} AS [CriterionIndex],
        [document].[PlanningApplicationId]
    FROM CONTAINSTABLE([dbo].[PlanningDocument], ([Name], [DocumentType], [ContentText]), @SearchCondition{index}) AS [matches]
    INNER JOIN [dbo].[PlanningDocument] AS [document]
        ON [document].[Id] = [matches].[KEY]");
    }

    private static void AppendMatchedApplicationSelect(StringBuilder sql, int index, bool includeRankAndFlags)
    {
        if (includeRankAndFlags)
        {
            sql.AppendLine($@"    SELECT
        {index} AS [CriterionIndex],
        [matches].[RANK] AS [SearchRank],
        [matches].[KEY] AS [PlanningApplicationId],
        CAST(1 AS int) AS [HasApplicationMatch],
        CAST(0 AS int) AS [HasDocumentMatch]
    FROM CONTAINSTABLE([dbo].[PlanningApplication], ([Title], [Description], [Address]), @SearchCondition{index}) AS [matches]");
            return;
        }

        sql.AppendLine($@"    SELECT
        {index} AS [CriterionIndex],
        [matches].[KEY] AS [PlanningApplicationId]
    FROM CONTAINSTABLE([dbo].[PlanningApplication], ([Title], [Description], [Address]), @SearchCondition{index}) AS [matches]");
    }

    private static async Task<int> ExecuteSearchCountAsync(
        ApplicationDbContext db,
        IReadOnlyList<string> searchConditions,
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
            command.CommandText = BuildKeywordSearchCountSql(searchConditions.Count);
            AddSearchConditionParameters(command, searchConditions);
            AddParameter(command, "@SearchCriteriaCount", searchConditions.Count);
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return result is null || result == DBNull.Value
                ? 0
                : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
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

    private static string? GetNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? GetNullableGuid(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static DateTime? GetNullableDateTime(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    private static List<string> ExtractSearchTerms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return KeywordRegex.Matches(value)
            .Select(match => match.Value)
            .Where(term => term.Length > 1 && !IgnoredSearchTerms.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(term => term.Length > 50 ? term[..50] : term)
            .ToList();
    }

    private static string BuildFullTextSearchCondition(IReadOnlyList<string> searchTerms, bool requireAdjacentWords)
    {
        var prefixTerms = searchTerms
            .Select(FormatFullTextPrefixTerm)
            .ToList();

        if (requireAdjacentWords && searchTerms.Count > 1)
        {
            return $"NEAR(({string.Join(", ", prefixTerms)}), 0, TRUE)";
        }

        return string.Join(" AND ", prefixTerms);
    }

    private static string FormatFullTextPrefixTerm(string term)
    {
        return "\"" + term + "*\"";
    }
}
