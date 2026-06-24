using System.Globalization;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

internal static class CompanyQuery
{
    private const string FullTextSearchAvailabilitySql = @"
SELECT CAST(
    CASE
        WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
            AND EXISTS (
                SELECT 1
                FROM sys.fulltext_indexes
                WHERE object_id = OBJECT_ID(N'dbo.Company')
            )
        THEN 1
        ELSE 0
    END AS int) AS [Value]";

    private const string CompanyNameFullTextMatchSql = @"
SELECT [KEY] AS [CompanyId], [RANK] AS [SearchRank]
FROM CONTAINSTABLE([dbo].[Company], ([CompanyName]), {0})";

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

    public static bool RequiresFullTextSearch(CompanySearchFilters filters)
    {
        return BuildCompanyNameSearchCondition(filters.CompanyName) is not null;
    }

    public static async Task<bool> IsFullTextSearchAvailableAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteScalarAsync(db, FullTextSearchAvailabilitySql, cancellationToken);

        return result is not null
            && result != DBNull.Value
            && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    public static IQueryable<CompanySearchQueryResult> CreateSearchQuery(
        ApplicationDbContext db,
        CompanySearchFilters filters)
    {
        var companyQuery = ApplyNonNameFilters(db.Companies.AsNoTracking(), filters);
        var companyName = filters.CompanyName?.Trim();
        var searchCondition = BuildCompanyNameSearchCondition(companyName);
        if (!string.IsNullOrWhiteSpace(companyName) && searchCondition is null)
        {
            return companyQuery
                .Where(_ => false)
                .Select(company => new CompanySearchQueryResult(company, null));
        }

        if (searchCondition is null)
        {
            return companyQuery.Select(company => new CompanySearchQueryResult(company, null));
        }

        var matches = db.Set<CompanyFullTextSearchMatch>()
            .FromSqlRaw(CompanyNameFullTextMatchSql, searchCondition)
            .AsNoTracking();

        return companyQuery.Join(
            matches,
            company => company.Id,
            match => match.CompanyId,
            (company, match) => new CompanySearchQueryResult(company, match.SearchRank));
    }

    private static IQueryable<Company> ApplyNonNameFilters(
        IQueryable<Company> query,
        CompanySearchFilters filters)
    {
        var sicCode = filters.SicCode?.Trim();
        if (!string.IsNullOrWhiteSpace(sicCode))
        {
            var sicPattern = $"{sicCode} -%";
            query = query.Where(company =>
                company.SicCodeSicText1 == sicCode
                || company.SicCodeSicText2 == sicCode
                || company.SicCodeSicText3 == sicCode
                || company.SicCodeSicText4 == sicCode
                || (company.SicCodeSicText1 != null && EF.Functions.Like(company.SicCodeSicText1, sicPattern))
                || (company.SicCodeSicText2 != null && EF.Functions.Like(company.SicCodeSicText2, sicPattern))
                || (company.SicCodeSicText3 != null && EF.Functions.Like(company.SicCodeSicText3, sicPattern))
                || (company.SicCodeSicText4 != null && EF.Functions.Like(company.SicCodeSicText4, sicPattern)));
        }

        if (filters.Location is not null)
        {
            var origin = CreatePoint(filters.Location);
            var radiusMeters = filters.Location.RadiusKm * 1000d;
            query = query.Where(company =>
                company.Location != null
                && company.Location.Distance(origin) <= radiusMeters);
        }

        return query;
    }

    public static IOrderedQueryable<CompanySearchQueryResult> ApplyDefaultSort(
        IQueryable<CompanySearchQueryResult> query,
        CompanySearchFilters? filters = null)
    {
        if (filters?.Location is not null)
        {
            var origin = CreatePoint(filters.Location);
            return query
                .OrderBy(result => result.Company.Location!.Distance(origin))
                .ThenByDescending(result => result.SearchRank ?? 0)
                .ThenBy(result => result.Company.CompanyName)
                .ThenBy(result => result.Company.CompanyNumber)
                .ThenBy(result => result.Company.Id);
        }

        if (RequiresFullTextSearch(filters ?? CompanySearchFilters.Empty))
        {
            return query
                .OrderByDescending(result => result.SearchRank ?? 0)
                .ThenBy(result => result.Company.CompanyName)
                .ThenBy(result => result.Company.CompanyNumber)
                .ThenBy(result => result.Company.Id);
        }

        return query
            .OrderBy(result => result.Company.CompanyName)
            .ThenBy(result => result.Company.CompanyNumber)
            .ThenBy(result => result.Company.Id);
    }

    public static IOrderedQueryable<CompanySearchQueryResult> ApplyPageSort(
        IQueryable<CompanySearchQueryResult> query,
        CompanySearchFilters? filters = null)
    {
        if (filters?.Location is not null)
        {
            var origin = CreatePoint(filters.Location);
            return query
                .OrderBy(result => result.Company.Location!.Distance(origin))
                .ThenByDescending(result => result.SearchRank ?? 0)
                .ThenBy(result => result.Company.Id);
        }

        if (RequiresFullTextSearch(filters ?? CompanySearchFilters.Empty))
        {
            return query
                .OrderByDescending(result => result.SearchRank ?? 0)
                .ThenBy(result => result.Company.CompanyName)
                .ThenBy(result => result.Company.CompanyNumber)
                .ThenBy(result => result.Company.Id);
        }

        return query.OrderBy(result => result.Company.Id);
    }

    public static Point CreatePoint(CompanyLocationSearch location)
    {
        return new Point(location.Longitude, location.Latitude)
        {
            SRID = 4326
        };
    }

    public static CompanySicCodeOption? ParseSicCodeOption(string? sicText)
    {
        if (string.IsNullOrWhiteSpace(sicText))
        {
            return null;
        }

        var text = sicText.Trim();
        var separatorIndex = text.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            return new CompanySicCodeOption(
                text[..separatorIndex].Trim(),
                text[(separatorIndex + 3)..].Trim());
        }

        var firstSpaceIndex = text.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpaceIndex > 0)
        {
            return new CompanySicCodeOption(
                text[..firstSpaceIndex].Trim(),
                text[(firstSpaceIndex + 1)..].Trim());
        }

        return new CompanySicCodeOption(text, string.Empty);
    }

    public static IEnumerable<CompanySicCodeOption> NormalizeSicCodeOptions(IEnumerable<string?> sicTexts)
    {
        return sicTexts
            .Select(ParseSicCodeOption)
            .OfType<CompanySicCodeOption>()
            .Where(option => !string.IsNullOrWhiteSpace(option.Code))
            .GroupBy(option => option.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(option => option.Description.Length)
                .ThenBy(option => option.Description, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(option => TryParseCodeNumber(option.Code, out var number) ? number : int.MaxValue)
            .ThenBy(option => option.Code, StringComparer.OrdinalIgnoreCase);
    }

    private static string? BuildCompanyNameSearchCondition(string? value)
    {
        var searchTerms = ExtractSearchTerms(value);
        if (searchTerms.Count == 0)
        {
            return null;
        }

        return string.Join(" AND ", searchTerms.Select(FormatFullTextPrefixTerm));
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

    private static string FormatFullTextPrefixTerm(string term)
    {
        return "\"" + term + "*\"";
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

    private static bool TryParseCodeNumber(string code, out int number)
    {
        return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }
}

internal sealed record CompanySearchQueryResult(
    Company Company,
    int? SearchRank);
