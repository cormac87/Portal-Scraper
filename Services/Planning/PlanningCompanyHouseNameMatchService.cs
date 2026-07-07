using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class PlanningCompanyHouseNameMatchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<PlanningCompanyHouseNameMatchService> logger) : IPlanningCompanyHouseNameMatchService
{
    private const int MaxCompanyNameWords = 12;
    private const int MinCompanyNameWords = 2;
    private const int LookupBatchSize = 700;

    private const string NormalizedCompanyNameColumnAvailabilitySql = @"
SELECT CAST(
    CASE
        WHEN COL_LENGTH(N'dbo.Company', N'NormalizedCompanyName') IS NOT NULL
        THEN 1
        ELSE 0
    END AS int) AS [Value]";

    private static readonly Regex TokenRegex = new(@"[\p{L}\p{Nd}]+|&", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> LegalSuffixWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CIC",
        "CIO",
        "LP",
        "LLP",
        "LTD",
        "LIMITED",
        "PLC"
    };

    private static readonly HashSet<string> IgnoredNamePartWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "A",
        "AN",
        "AND",
        "AS",
        "AT",
        "BY",
        "FOR",
        "FROM",
        "IN",
        "OF",
        "ON",
        "OR",
        "THE",
        "TO",
        "WITH"
    };

    public async Task<PlanningCompanyHouseNameMatchResult> FindCompanyHouseNamesAsync(
        PlanningApplication application,
        CancellationToken cancellationToken = default)
    {
        var documents = application.PlanningDocuments
            .Where(document => !string.IsNullOrWhiteSpace(document.ContentText))
            .OrderByDescending(document => document.PublishedDate)
            .ThenBy(document => document.Name)
            .Select(document => new MatchDocument(document.Id, document.Name, document.ContentText!))
            .ToList();
        var skippedDocumentCount = application.PlanningDocuments.Count - documents.Count;

        if (documents.Count == 0)
        {
            return new PlanningCompanyHouseNameMatchResult([], 0, skippedDocumentCount, 0, 0);
        }

        var candidateIndex = BuildCandidateIndex(documents);
        if (candidateIndex.Count == 0)
        {
            return new PlanningCompanyHouseNameMatchResult([], documents.Count, skippedDocumentCount, 0, 0);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hasNormalizedCompanyNameColumn = await HasNormalizedCompanyNameColumnAsync(db, cancellationToken);
        var companyMatches = hasNormalizedCompanyNameColumn
            ? await FindCompaniesByNormalizedNameAsync(db, candidateIndex, cancellationToken)
            : await FindCompaniesByExactNameAsync(db, candidateIndex, cancellationToken);

        logger.LogInformation(
            "Matched {CompanyCount} Companies House names from {CandidateCount} candidate names across {DocumentCount} planning documents for application {PlanningApplicationId}",
            companyMatches.Count,
            candidateIndex.Count,
            documents.Count,
            application.Id);

        return new PlanningCompanyHouseNameMatchResult(
            companyMatches,
            documents.Count,
            skippedDocumentCount,
            candidateIndex.Count,
            companyMatches.Sum(company => company.MentionCount));
    }

    private static Dictionary<string, CandidateNameAccumulator> BuildCandidateIndex(
        IReadOnlyList<MatchDocument> documents)
    {
        var candidateIndex = new Dictionary<string, CandidateNameAccumulator>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            var tokens = Tokenize(document.Text);
            if (tokens.Count < MinCompanyNameWords)
            {
                continue;
            }

            for (var endIndex = 0; endIndex < tokens.Count; endIndex++)
            {
                if (!IsLegalSuffixEnd(tokens, endIndex))
                {
                    continue;
                }

                var firstStartIndex = Math.Max(0, endIndex - MaxCompanyNameWords + 1);
                for (var startIndex = firstStartIndex; startIndex <= endIndex - MinCompanyNameWords + 1; startIndex++)
                {
                    if (!HasSignificantNamePart(tokens, startIndex, endIndex))
                    {
                        continue;
                    }

                    AddCandidateVariants(candidateIndex, document, tokens, startIndex, endIndex);
                }
            }
        }

        return candidateIndex;
    }

    private static List<TextToken> Tokenize(string text)
    {
        return TokenRegex.Matches(text)
            .Select(match => new TextToken(
                match.Value,
                NormalizeToken(match.Value),
                match.Index,
                match.Index + match.Length))
            .Where(token => !string.IsNullOrWhiteSpace(token.Normalized))
            .ToList();
    }

    private static bool IsLegalSuffixEnd(IReadOnlyList<TextToken> tokens, int endIndex)
    {
        var word = tokens[endIndex].Normalized;
        if (LegalSuffixWords.Contains(word))
        {
            return true;
        }

        return string.Equals(word, "PARTNERSHIP", StringComparison.OrdinalIgnoreCase)
            && endIndex >= 2
            && string.Equals(tokens[endIndex - 1].Normalized, "LIABILITY", StringComparison.OrdinalIgnoreCase)
            && string.Equals(tokens[endIndex - 2].Normalized, "LIMITED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSignificantNamePart(IReadOnlyList<TextToken> tokens, int startIndex, int endIndex)
    {
        var suffixStartIndex = GetLegalSuffixStartIndex(tokens, endIndex);
        if (suffixStartIndex <= startIndex)
        {
            return false;
        }

        for (var index = startIndex; index < suffixStartIndex; index++)
        {
            var word = tokens[index].Normalized;
            if (word.Length > 1 && !IgnoredNamePartWords.Contains(word))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetLegalSuffixStartIndex(IReadOnlyList<TextToken> tokens, int endIndex)
    {
        return string.Equals(tokens[endIndex].Normalized, "PARTNERSHIP", StringComparison.OrdinalIgnoreCase)
            && endIndex >= 2
            && string.Equals(tokens[endIndex - 1].Normalized, "LIABILITY", StringComparison.OrdinalIgnoreCase)
            && string.Equals(tokens[endIndex - 2].Normalized, "LIMITED", StringComparison.OrdinalIgnoreCase)
            ? endIndex - 2
            : endIndex;
    }

    private static void AddCandidateVariants(
        Dictionary<string, CandidateNameAccumulator> candidateIndex,
        MatchDocument document,
        IReadOnlyList<TextToken> tokens,
        int startIndex,
        int endIndex)
    {
        var words = tokens
            .Skip(startIndex)
            .Take(endIndex - startIndex + 1)
            .Select(token => token.Normalized)
            .ToList();
        var variants = BuildCandidateVariants(words);
        var start = tokens[startIndex].Start;
        var end = tokens[endIndex].End;

        foreach (var variant in variants)
        {
            if (variant.LookupKey.Length < 3)
            {
                continue;
            }

            if (!candidateIndex.TryGetValue(variant.LookupKey, out var accumulator))
            {
                accumulator = new CandidateNameAccumulator(variant.LookupKey);
                candidateIndex.Add(variant.LookupKey, accumulator);
            }

            foreach (var exactName in variant.ExactNames)
            {
                accumulator.ExactNames.Add(exactName);
            }

            accumulator.AddOccurrence(document.Id, document.Name, start, end);
        }
    }

    private static IReadOnlyList<CandidateVariant> BuildCandidateVariants(IReadOnlyList<string> words)
    {
        var wordVariants = new List<IReadOnlyList<string>> { words };
        var suffixVariants = BuildLegalSuffixVariants(words);
        foreach (var suffixVariant in suffixVariants)
        {
            if (!wordVariants.Any(existing => WordsEqual(existing, suffixVariant)))
            {
                wordVariants.Add(suffixVariant);
            }
        }

        var variants = new Dictionary<string, CandidateVariantBuilder>(StringComparer.Ordinal);
        foreach (var wordVariant in wordVariants)
        {
            var lookupKey = BuildLookupKey(wordVariant);
            if (string.IsNullOrWhiteSpace(lookupKey))
            {
                continue;
            }

            if (!variants.TryGetValue(lookupKey, out var builder))
            {
                builder = new CandidateVariantBuilder(lookupKey);
                variants.Add(lookupKey, builder);
            }

            foreach (var exactName in BuildExactNameVariants(wordVariant))
            {
                builder.ExactNames.Add(exactName);
            }
        }

        return variants.Values
            .Select(builder => new CandidateVariant(builder.LookupKey, builder.ExactNames.ToList()))
            .ToList();
    }

    private static List<IReadOnlyList<string>> BuildLegalSuffixVariants(IReadOnlyList<string> words)
    {
        var variants = new List<IReadOnlyList<string>>();
        if (words.Count == 0)
        {
            return variants;
        }

        var lastWord = words[^1];
        if (string.Equals(lastWord, "LTD", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add([.. words.Take(words.Count - 1), "LIMITED"]);
        }
        else if (string.Equals(lastWord, "LIMITED", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add([.. words.Take(words.Count - 1), "LTD"]);
        }

        if (words.Count >= 3
            && string.Equals(words[^3], "LIMITED", StringComparison.OrdinalIgnoreCase)
            && string.Equals(words[^2], "LIABILITY", StringComparison.OrdinalIgnoreCase)
            && string.Equals(words[^1], "PARTNERSHIP", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add([.. words.Take(words.Count - 3), "LLP"]);
        }
        else if (string.Equals(lastWord, "LLP", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add([.. words.Take(words.Count - 1), "LIMITED", "LIABILITY", "PARTNERSHIP"]);
        }

        return variants;
    }

    private static IEnumerable<string> BuildExactNameVariants(IReadOnlyList<string> words)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            string.Join(' ', words)
        };

        if (words.Any(word => string.Equals(word, "AND", StringComparison.OrdinalIgnoreCase)))
        {
            var ampersandWords = words
                .Select(word => string.Equals(word, "AND", StringComparison.OrdinalIgnoreCase) ? "&" : word)
                .ToList();
            variants.Add(string.Join(' ', ampersandWords));
        }

        return variants;
    }

    private async Task<IReadOnlyList<PlanningCompanyHouseNameMatch>> FindCompaniesByNormalizedNameAsync(
        ApplicationDbContext db,
        IReadOnlyDictionary<string, CandidateNameAccumulator> candidateIndex,
        CancellationToken cancellationToken)
    {
        var companies = new List<CompanyLookupResult>();
        var keys = candidateIndex.Keys.ToList();

        foreach (var batch in keys.Chunk(LookupBatchSize))
        {
            companies.AddRange(await ExecuteCompanyLookupAsync(
                db,
                batch,
                useNormalizedCompanyName: true,
                cancellationToken));
        }

        return BuildCompanyMatches(companies, candidateIndex);
    }

    private async Task<IReadOnlyList<PlanningCompanyHouseNameMatch>> FindCompaniesByExactNameAsync(
        ApplicationDbContext db,
        IReadOnlyDictionary<string, CandidateNameAccumulator> candidateIndex,
        CancellationToken cancellationToken)
    {
        var exactNameMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidateIndex.Values)
        {
            foreach (var exactName in candidate.ExactNames)
            {
                if (!exactNameMap.TryGetValue(exactName, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    exactNameMap.Add(exactName, keys);
                }

                keys.Add(candidate.LookupKey);
            }
        }

        var companies = new List<CompanyLookupResult>();
        foreach (var batch in exactNameMap.Keys.Chunk(LookupBatchSize))
        {
            companies.AddRange(await ExecuteCompanyLookupAsync(
                db,
                batch,
                useNormalizedCompanyName: false,
                cancellationToken));
        }

        return BuildCompanyMatches(companies, candidateIndex);
    }

    private static IReadOnlyList<PlanningCompanyHouseNameMatch> BuildCompanyMatches(
        IReadOnlyList<CompanyLookupResult> companies,
        IReadOnlyDictionary<string, CandidateNameAccumulator> candidateIndex)
    {
        var matches = new Dictionary<Guid, CompanyMatchAccumulator>();
        foreach (var company in companies)
        {
            var lookupKey = company.LookupKey;
            if (string.IsNullOrWhiteSpace(lookupKey)
                || !candidateIndex.TryGetValue(lookupKey, out var candidate))
            {
                continue;
            }

            if (!matches.TryGetValue(company.Id, out var match))
            {
                match = new CompanyMatchAccumulator(company);
                matches.Add(company.Id, match);
            }

            match.AddCandidate(candidate);
        }

        return matches.Values
            .Select(match => match.ToMatch())
            .OrderByDescending(match => match.MentionCount)
            .ThenBy(match => match.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.CompanyNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<CompanyLookupResult>> ExecuteCompanyLookupAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<string> lookupValues,
        bool useNormalizedCompanyName,
        CancellationToken cancellationToken)
    {
        var results = new List<CompanyLookupResult>();
        if (lookupValues.Count == 0)
        {
            return results;
        }

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
            command.CommandText = BuildCompanyLookupSql(lookupValues.Count, useNormalizedCompanyName);
            AddLookupParameters(command, lookupValues);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var companyName = GetNullableString(reader, "CompanyName") ?? string.Empty;
                var lookupKey = useNormalizedCompanyName
                    ? GetNullableString(reader, "LookupKey") ?? string.Empty
                    : BuildLookupKey(TokenizeName(companyName));

                results.Add(new CompanyLookupResult(
                    reader.GetGuid(reader.GetOrdinal("Id")),
                    companyName,
                    reader.GetString(reader.GetOrdinal("CompanyNumber")),
                    GetNullableString(reader, "CompanyStatus"),
                    lookupKey));
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

    private static string BuildCompanyLookupSql(int lookupValueCount, bool useNormalizedCompanyName)
    {
        var parameterNames = Enumerable
            .Range(0, lookupValueCount)
            .Select(index => $"@LookupValue{index}");
        var lookupColumn = useNormalizedCompanyName
            ? "[NormalizedCompanyName]"
            : "[CompanyName]";
        var lookupKeySelect = useNormalizedCompanyName
            ? ", [NormalizedCompanyName] AS [LookupKey]"
            : string.Empty;

        return $@"
SELECT
    [Id],
    [CompanyName],
    [CompanyNumber],
    [CompanyStatus]
    {lookupKeySelect}
FROM [dbo].[Company]
WHERE {lookupColumn} IN ({string.Join(", ", parameterNames)})";
    }

    private static void AddLookupParameters(DbCommand command, IReadOnlyCollection<string> lookupValues)
    {
        var index = 0;
        foreach (var lookupValue in lookupValues)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@LookupValue{index}";
            parameter.Value = lookupValue;
            command.Parameters.Add(parameter);
            index++;
        }
    }

    private static async Task<bool> HasNormalizedCompanyNameColumnAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteScalarAsync(db, NormalizedCompanyNameColumnAvailabilitySql, cancellationToken);

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

    private static string? GetNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static List<string> TokenizeName(string value)
    {
        return TokenRegex.Matches(value)
            .Select(match => NormalizeToken(match.Value))
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToList();
    }

    private static string NormalizeToken(string value)
    {
        return value == "&"
            ? "AND"
            : value.ToUpperInvariant();
    }

    private static string BuildLookupKey(IEnumerable<string> words)
    {
        return string.Concat(words.SelectMany(NormalizeLookupKeyCharacters));
    }

    private static IEnumerable<char> NormalizeLookupKeyCharacters(string word)
    {
        var normalizedWord = string.Equals(word, "&", StringComparison.Ordinal)
            ? "AND"
            : word;

        foreach (var character in normalizedWord.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                yield return character;
            }
        }
    }

    private static bool WordsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        return left.Count == right.Count
            && left.Zip(right).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record MatchDocument(Guid Id, string Name, string Text);

    private sealed record TextToken(string Text, string Normalized, int Start, int End);

    private sealed record CandidateVariant(string LookupKey, IReadOnlyList<string> ExactNames);

    private sealed class CandidateVariantBuilder(string lookupKey)
    {
        public string LookupKey { get; } = lookupKey;

        public HashSet<string> ExactNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CandidateNameAccumulator(string lookupKey)
    {
        private readonly Dictionary<Guid, CandidateDocumentAccumulator> _documents = [];

        public string LookupKey { get; } = lookupKey;

        public HashSet<string> ExactNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<CandidateDocumentAccumulator> Documents => _documents.Values;

        public int MentionCount => _documents.Values.Sum(document => document.MentionCount);

        public void AddOccurrence(Guid documentId, string documentName, int start, int end)
        {
            if (!_documents.TryGetValue(documentId, out var document))
            {
                document = new CandidateDocumentAccumulator(documentId, documentName);
                _documents.Add(documentId, document);
            }

            document.Spans.Add($"{start}:{end}");
        }
    }

    private sealed class CandidateDocumentAccumulator(Guid documentId, string documentName)
    {
        public Guid DocumentId { get; } = documentId;

        public string DocumentName { get; } = documentName;

        public HashSet<string> Spans { get; } = new(StringComparer.Ordinal);

        public int MentionCount => Spans.Count;
    }

    private sealed record CompanyLookupResult(
        Guid Id,
        string CompanyName,
        string CompanyNumber,
        string? CompanyStatus,
        string LookupKey);

    private sealed class CompanyMatchAccumulator(CompanyLookupResult company)
    {
        private readonly Dictionary<Guid, CandidateDocumentAccumulator> _documents = [];
        private readonly HashSet<string> _candidateKeys = new(StringComparer.Ordinal);

        public void AddCandidate(CandidateNameAccumulator candidate)
        {
            if (!_candidateKeys.Add(candidate.LookupKey))
            {
                return;
            }

            foreach (var candidateDocument in candidate.Documents)
            {
                if (!_documents.TryGetValue(candidateDocument.DocumentId, out var document))
                {
                    document = new CandidateDocumentAccumulator(
                        candidateDocument.DocumentId,
                        candidateDocument.DocumentName);
                    _documents.Add(candidateDocument.DocumentId, document);
                }

                foreach (var span in candidateDocument.Spans)
                {
                    document.Spans.Add(span);
                }
            }
        }

        public PlanningCompanyHouseNameMatch ToMatch()
        {
            var documents = _documents.Values
                .OrderByDescending(document => document.MentionCount)
                .ThenBy(document => document.DocumentName, StringComparer.OrdinalIgnoreCase)
                .Select(document => new PlanningCompanyHouseNameDocumentMatch(
                    document.DocumentId,
                    document.DocumentName,
                    document.MentionCount))
                .ToList();

            return new PlanningCompanyHouseNameMatch(
                company.Id,
                company.CompanyName,
                company.CompanyNumber,
                company.CompanyStatus,
                documents.Sum(document => document.MentionCount),
                documents.Count,
                documents);
        }
    }
}
