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
        PlanningApplicationSearchFilters? filters = null,
        CancellationToken cancellationToken = default)
    {
        var searchFilters = NormalizeFilters(filters);
        var normalizedPageSize = Math.Max(1, pageSize);
        if (criteria.Count == 0 && !searchFilters.HasActive)
        {
            return new PlanningKeywordSearchPage([], 0, 1, IsFullTextSearchAvailable: true);
        }

        if (searchFilters.HasEmptyAuthorityFilter)
        {
            return new PlanningKeywordSearchPage([], 0, 1, IsFullTextSearchAvailable: true);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (criteria.Count == 0)
        {
            return await SearchFilteredApplicationsAsync(
                db,
                searchFilters,
                page,
                normalizedPageSize,
                cancellationToken);
        }

        if (!await IsFullTextSearchAvailableAsync(db, cancellationToken))
        {
            return new PlanningKeywordSearchPage([], 0, 1, IsFullTextSearchAvailable: false);
        }

        var searchConditions = criteria
            .Select(criterion => criterion.SearchCondition)
            .ToList();
        return await ExecuteSearchPageAsync(
            db,
            searchConditions,
            searchFilters,
            page,
            normalizedPageSize,
            cancellationToken);
    }

    private static NormalizedSearchFilters NormalizeFilters(PlanningApplicationSearchFilters? filters)
    {
        if (filters is null)
        {
            return NormalizedSearchFilters.Empty;
        }

        var authorityIds = filters.PlanningAuthorityIds is null
            ? null
            : filters.PlanningAuthorityIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

        return new NormalizedSearchFilters(
            authorityIds,
            filters.StartDate?.Date,
            filters.EndDate?.Date.AddDays(1));
    }

    private static async Task<PlanningKeywordSearchPage> SearchFilteredApplicationsAsync(
        ApplicationDbContext db,
        NormalizedSearchFilters filters,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = ApplyApplicationFilters(db.PlanningApplications
            .AsNoTracking()
            .Include(application => application.PlanningAuthority), filters);

        var totalCount = await query.CountAsync(cancellationToken);
        var currentPage = PlanningPagination.ClampPage(page, totalCount, pageSize);
        var results = await query
            .OrderByDescending(application => application.ValidatedDate)
            .ThenByDescending(application => application.ReceivedDate)
            .ThenBy(application => application.ApplicationReference)
            .ThenBy(application => application.Title)
            .ThenBy(application => application.Id)
            .Skip(PlanningPagination.GetPageSkip(currentPage, pageSize))
            .Take(pageSize)
            .Select(application => new PlanningSearchResult
            {
                MatchType = "Application",
                PlanningApplicationId = application.Id,
                PlanningAuthorityName = application.PlanningAuthority.Name,
                ApplicationReference = application.ApplicationReference,
                ApplicationTitle = application.Title,
                Status = application.Status,
                Address = application.Address,
                ReceivedDate = application.ReceivedDate,
                ValidatedDate = application.ValidatedDate,
                Url = application.SourceUrl,
                SearchRank = 0
            })
            .ToListAsync(cancellationToken);

        return new PlanningKeywordSearchPage(results, totalCount, currentPage, IsFullTextSearchAvailable: true);
    }

    private static IQueryable<PlanningApplication> ApplyApplicationFilters(
        IQueryable<PlanningApplication> query,
        NormalizedSearchFilters filters)
    {
        if (filters.AuthorityIds is not null)
        {
            query = query.Where(application => filters.AuthorityIds.Contains(application.PlanningAuthorityId));
        }

        if (filters.StartDateInclusive.HasValue)
        {
            query = query.Where(application =>
                (application.ValidatedDate ?? application.ReceivedDate) >= filters.StartDateInclusive.Value);
        }

        if (filters.EndDateExclusive.HasValue)
        {
            query = query.Where(application =>
                (application.ValidatedDate ?? application.ReceivedDate) < filters.EndDateExclusive.Value);
        }

        return query;
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

    private static async Task<PlanningKeywordSearchPage> ExecuteSearchPageAsync(
        ApplicationDbContext db,
        IReadOnlyList<string> searchConditions,
        NormalizedSearchFilters filters,
        int requestedPage,
        int take,
        CancellationToken cancellationToken)
    {
        var results = new List<PlanningSearchResult>();
        var totalCount = 0;
        var currentPage = 1;
        var connection = db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State == ConnectionState.Closed;

        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            ApplyConfiguredCommandTimeout(db, command);
            command.CommandText = BuildKeywordSearchPageSql(searchConditions.Count, filters);
            AddSearchConditionParameters(command, searchConditions);
            AddFilterParameters(command, filters);
            AddParameter(command, "@SearchCriteriaCount", searchConditions.Count);
            AddParameter(command, "@RequestedPage", requestedPage);
            AddParameter(command, "@Take", take);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                totalCount = reader.GetInt32(reader.GetOrdinal("TotalCount"));
                currentPage = reader.GetInt32(reader.GetOrdinal("CurrentPage"));
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

        return new PlanningKeywordSearchPage(results, totalCount, currentPage, IsFullTextSearchAvailable: true);
    }

    private static string BuildKeywordSearchPageSql(int searchCriteriaCount, NormalizedSearchFilters filters)
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
),
NumberedApplications AS
(
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
        CAST(NULL AS nvarchar(max)) AS [PreviewText],
        CAST(COUNT_BIG(*) OVER() AS int) AS [TotalCount],
        ROW_NUMBER() OVER (
            ORDER BY [RankedApplications].[SearchRank] DESC,
                [application].[ValidatedDate] DESC,
                [application].[ReceivedDate] DESC,
                [application].[ApplicationReference],
                [application].[Title],
                [application].[Id]) AS [RowNumber]
    FROM [RankedApplications]
        INNER JOIN [dbo].[PlanningApplication] AS [application]
            ON [application].[Id] = [RankedApplications].[PlanningApplicationId]
        INNER JOIN [dbo].[PlanningAuthority] AS [authority]
            ON [authority].[Id] = [application].[PlanningAuthorityId]
");
        AppendApplicationFilterWhereClause(sql, filters);
        sql.AppendLine(@"),
PagedApplications AS
(
    SELECT
        [MatchType],
        [PlanningApplicationId],
        [PlanningDocumentId],
        [PlanningAuthorityName],
        [ApplicationReference],
        [ApplicationTitle],
        [Status],
        [Address],
        [ReceivedDate],
        [ValidatedDate],
        [DocumentName],
        [DocumentType],
        [Url],
        [PublishedDate],
        [SearchRank],
        [PreviewText],
        [TotalCount],
        [RowNumber],
        CASE
            WHEN @RequestedPage < 1 THEN 1
            WHEN @RequestedPage > CEILING([TotalCount] / CONVERT(float, @Take))
                THEN CONVERT(int, CEILING([TotalCount] / CONVERT(float, @Take)))
            ELSE @RequestedPage
        END AS [CurrentPage]
    FROM [NumberedApplications]
)
SELECT
    [MatchType],
    [PlanningApplicationId],
    [PlanningDocumentId],
    [PlanningAuthorityName],
    [ApplicationReference],
    [ApplicationTitle],
    [Status],
    [Address],
    [ReceivedDate],
    [ValidatedDate],
    [DocumentName],
    [DocumentType],
    [Url],
    [PublishedDate],
    [SearchRank],
    [PreviewText],
    [TotalCount],
    [CurrentPage]
FROM [PagedApplications]
WHERE [RowNumber] > (([CurrentPage] - 1) * @Take)
    AND [RowNumber] <= ([CurrentPage] * @Take)
ORDER BY [RowNumber]
OPTION (MAXDOP 1)");

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
            ApplyConfiguredCommandTimeout(db, command);
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

    private static void ApplyConfiguredCommandTimeout(ApplicationDbContext db, DbCommand command)
    {
        var commandTimeout = db.Database.GetCommandTimeout();
        if (commandTimeout.HasValue)
        {
            command.CommandTimeout = commandTimeout.Value;
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

    private static void AddFilterParameters(DbCommand command, NormalizedSearchFilters filters)
    {
        if (filters.AuthorityIds is not null)
        {
            for (var index = 0; index < filters.AuthorityIds.Count; index++)
            {
                AddParameter(command, $"@AuthorityId{index}", filters.AuthorityIds[index]);
            }
        }

        if (filters.StartDateInclusive.HasValue)
        {
            AddParameter(command, "@ApplicationDateStart", filters.StartDateInclusive.Value);
        }

        if (filters.EndDateExclusive.HasValue)
        {
            AddParameter(command, "@ApplicationDateEndExclusive", filters.EndDateExclusive.Value);
        }
    }

    private static void AppendApplicationFilterWhereClause(StringBuilder sql, NormalizedSearchFilters filters)
    {
        var conditions = new List<string>();
        if (filters.AuthorityIds is not null)
        {
            var parameterNames = Enumerable
                .Range(0, filters.AuthorityIds.Count)
                .Select(index => $"@AuthorityId{index}");

            conditions.Add($"[application].[PlanningAuthorityId] IN ({string.Join(", ", parameterNames)})");
        }

        if (filters.StartDateInclusive.HasValue)
        {
            conditions.Add("COALESCE([application].[ValidatedDate], [application].[ReceivedDate]) >= @ApplicationDateStart");
        }

        if (filters.EndDateExclusive.HasValue)
        {
            conditions.Add("COALESCE([application].[ValidatedDate], [application].[ReceivedDate]) < @ApplicationDateEndExclusive");
        }

        if (conditions.Count > 0)
        {
            sql.AppendLine($"WHERE {string.Join(" AND ", conditions)}");
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

    private sealed record NormalizedSearchFilters(
        IReadOnlyList<Guid>? AuthorityIds,
        DateTime? StartDateInclusive,
        DateTime? EndDateExclusive)
    {
        public static NormalizedSearchFilters Empty { get; } = new(null, null, null);

        public bool HasActive => AuthorityIds is not null
            || StartDateInclusive.HasValue
            || EndDateExclusive.HasValue;

        public bool HasEmptyAuthorityFilter => AuthorityIds is { Count: 0 };
    }
}
