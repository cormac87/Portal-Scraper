using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class OllamaPlanningLlmCompanyExtractionService(
    HttpClient httpClient,
    IOptions<PlanningLlmCompanyExtractionOptions> options,
    IPlanningCompanyHouseNameMatchService companyNameMatchService,
    ILogger<OllamaPlanningLlmCompanyExtractionService> logger) : IPlanningLlmCompanyExtractionService
{
    private const string SystemPrompt = """
You extract UK registered company names from planning application document text.
Return only JSON. Do not include commentary.
Find company names exactly as written when possible.
Include registered business names and trading companies.
Do not include people, councils, planning authorities, addresses, document titles, roles, or generic phrases.
""";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PlanningLlmCompanyExtractionResult> FindCompaniesAsync(
        PlanningApplication application,
        CancellationToken cancellationToken = default)
    {
        var configuredOptions = options.Value;
        var documents = application.PlanningDocuments
            .Where(document => !string.IsNullOrWhiteSpace(document.ContentText))
            .OrderByDescending(document => document.PublishedDate)
            .ThenBy(document => document.Name)
            .Take(Math.Max(1, configuredOptions.MaxDocuments))
            .Select(document => new LlmDocument(document.Id, document.Name, document.ContentText!))
            .ToList();
        var skippedDocumentCount = application.PlanningDocuments.Count - documents.Count;

        if (documents.Count == 0)
        {
            var emptyConfirmed = new PlanningCompanyHouseNameMatchResult([], 0, skippedDocumentCount, 0, 0);
            return new PlanningLlmCompanyExtractionResult([], emptyConfirmed, 0, skippedDocumentCount, 0, configuredOptions.Model);
        }

        ConfigureHttpClient(configuredOptions);

        var suggestions = new Dictionary<string, SuggestionAccumulator>(StringComparer.OrdinalIgnoreCase);
        var chunkCount = 0;
        foreach (var document in documents)
        {
            var chunks = SplitIntoChunks(document.Text, configuredOptions.MaxChunkCharacters)
                .Take(Math.Max(1, configuredOptions.MaxChunksPerDocument))
                .ToList();

            foreach (var chunk in chunks)
            {
                chunkCount++;
                var names = await ExtractNamesFromChunkAsync(
                    configuredOptions,
                    document.Name,
                    chunk,
                    cancellationToken);

                foreach (var name in names)
                {
                    var cleanedName = CleanSuggestedName(name);
                    if (string.IsNullOrWhiteSpace(cleanedName))
                    {
                        continue;
                    }

                    if (!suggestions.TryGetValue(cleanedName, out var suggestion))
                    {
                        suggestion = new SuggestionAccumulator(cleanedName);
                        suggestions.Add(cleanedName, suggestion);
                    }

                    suggestion.AddMention(
                        document.Id,
                        document.Name,
                        Math.Max(1, CountNameMentions(chunk, cleanedName)));
                }
            }
        }

        var suggestionList = suggestions.Values
            .Select(suggestion => suggestion.ToSuggestion())
            .OrderByDescending(suggestion => suggestion.MentionCount)
            .ThenBy(suggestion => suggestion.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidates = suggestionList
            .SelectMany(suggestion => suggestion.Documents.Select(document => new PlanningCompanyHouseNameCandidate(
                document.DocumentId,
                document.DocumentName,
                suggestion.Name,
                document.MentionCount)))
            .ToList();
        var confirmedCompanies = await companyNameMatchService.MatchCandidateNamesAsync(
            candidates,
            documents.Count,
            skippedDocumentCount,
            cancellationToken);

        logger.LogInformation(
            "LLM extracted {SuggestionCount} company name suggestions and confirmed {ConfirmedCompanyCount} Companies House names for planning application {PlanningApplicationId}",
            suggestionList.Count,
            confirmedCompanies.Companies.Count,
            application.Id);

        return new PlanningLlmCompanyExtractionResult(
            suggestionList,
            confirmedCompanies,
            documents.Count,
            skippedDocumentCount,
            chunkCount,
            configuredOptions.Model);
    }

    private async Task<IReadOnlyList<string>> ExtractNamesFromChunkAsync(
        PlanningLlmCompanyExtractionOptions configuredOptions,
        string documentName,
        string chunk,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, configuredOptions.TimeoutSeconds)));

        var prompt = BuildPrompt(documentName, chunk, configuredOptions.MaxSuggestedNamesPerChunk);
        var request = new OllamaGenerateRequest(
            configuredOptions.Model,
            prompt,
            SystemPrompt,
            Stream: false,
            Format: "json",
            Options: new OllamaGenerateOptions(Temperature: 0, NumPredict: 512));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(
                "/api/generate",
                request,
                JsonOptions,
                timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                $"Unable to contact the local Ollama service at '{configuredOptions.EndpointUrl}'. Make sure Ollama is running and the model '{configuredOptions.Model}' is installed.",
                ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama returned HTTP {(int)response.StatusCode}: {TrimForError(responseText)}");
        }

        var generateResponse = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(
            JsonOptions,
            timeout.Token);
        var responseTextValue = generateResponse?.Response;
        if (string.IsNullOrWhiteSpace(responseTextValue))
        {
            return [];
        }

        return ParseCompanyNames(responseTextValue)
            .Take(Math.Max(1, configuredOptions.MaxSuggestedNamesPerChunk))
            .ToList();
    }

    private static string BuildPrompt(string documentName, string chunk, int maxSuggestedNamesPerChunk)
    {
        return $$"""
Document name: {{documentName}}

Extract up to {{Math.Max(1, maxSuggestedNamesPerChunk)}} company names from the text below.
Return this exact JSON shape:
{"companies":["Company Name Limited"]}
If there are no company names, return:
{"companies":[]}

Text:
{{chunk}}
""";
    }

    private static IReadOnlyList<string> ParseCompanyNames(string responseText)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<LlmCompanyResponse>(responseText, JsonOptions);
            return parsed?.Companies?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            var jsonStart = responseText.IndexOf('{', StringComparison.Ordinal);
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
            {
                return [];
            }

            var json = responseText[jsonStart..(jsonEnd + 1)];
            try
            {
                var parsed = JsonSerializer.Deserialize<LlmCompanyResponse>(json, JsonOptions);
                return parsed?.Companies?
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private static IReadOnlyList<string> SplitIntoChunks(string text, int maxChunkCharacters)
    {
        var normalizedMax = Math.Clamp(maxChunkCharacters, 1000, 20000);
        if (text.Length <= normalizedMax)
        {
            return [text];
        }

        var chunks = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var remaining = text.Length - index;
            var length = Math.Min(normalizedMax, remaining);
            var end = index + length;
            if (end < text.Length)
            {
                var searchStart = end - 1;
                var paragraphBreak = text.LastIndexOf("\n\n", searchStart, length, StringComparison.Ordinal);
                var sentenceBreak = text.LastIndexOf(". ", searchStart, length, StringComparison.Ordinal);
                var breakIndex = Math.Max(paragraphBreak, sentenceBreak);
                if (breakIndex > index + (length / 2))
                {
                    end = breakIndex + 1;
                }
            }

            chunks.Add(text[index..end]);
            index = end;
        }

        return chunks;
    }

    private static string? CleanSuggestedName(string value)
    {
        var cleaned = Regex.Replace(value.Trim(), @"\s+", " ");
        cleaned = cleaned.Trim(' ', '.', ',', ';', ':', '-', '"', '\'', '(', ')', '[', ']');
        if (cleaned.Length < 3 || cleaned.Length > 200)
        {
            return null;
        }

        if (!Regex.IsMatch(cleaned, @"[\p{L}\p{Nd}]", RegexOptions.CultureInvariant))
        {
            return null;
        }

        return cleaned;
    }

    private static int CountNameMentions(string text, string name)
    {
        var pattern = Regex.Escape(name).Replace(@"\ ", @"\s+");
        var count = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        return count == 0 ? 1 : count;
    }

    private void ConfigureHttpClient(PlanningLlmCompanyExtractionOptions configuredOptions)
    {
        if (httpClient.BaseAddress is null)
        {
            httpClient.BaseAddress = new Uri(configuredOptions.EndpointUrl.TrimEnd('/'));
        }
    }

    private static string TrimForError(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 1000 ? trimmed : string.Concat(trimmed.AsSpan(0, 1000), "...");
    }

    private sealed record LlmDocument(Guid Id, string Name, string Text);

    private sealed record LlmCompanyResponse(IReadOnlyList<string>? Companies);

    private sealed record OllamaGenerateRequest(
        string Model,
        string Prompt,
        string System,
        bool Stream,
        string Format,
        OllamaGenerateOptions Options);

    private sealed record OllamaGenerateOptions(
        double Temperature,
        [property: JsonPropertyName("num_predict")]
        int NumPredict);

    private sealed record OllamaGenerateResponse(string? Response);

    private sealed class SuggestionAccumulator(string name)
    {
        private readonly Dictionary<Guid, SuggestionDocumentAccumulator> _documents = [];

        public void AddMention(Guid documentId, string documentName, int mentionCount)
        {
            if (!_documents.TryGetValue(documentId, out var document))
            {
                document = new SuggestionDocumentAccumulator(documentId, documentName);
                _documents.Add(documentId, document);
            }

            document.MentionCount += mentionCount;
        }

        public PlanningLlmCompanySuggestion ToSuggestion()
        {
            var documents = _documents.Values
                .OrderByDescending(document => document.MentionCount)
                .ThenBy(document => document.DocumentName, StringComparer.OrdinalIgnoreCase)
                .Select(document => new PlanningLlmCompanySuggestionDocument(
                    document.DocumentId,
                    document.DocumentName,
                    document.MentionCount))
                .ToList();

            return new PlanningLlmCompanySuggestion(
                name,
                documents.Sum(document => document.MentionCount),
                documents.Count,
                documents);
        }
    }

    private sealed class SuggestionDocumentAccumulator(Guid documentId, string documentName)
    {
        public Guid DocumentId { get; } = documentId;

        public string DocumentName { get; } = documentName;

        public int MentionCount { get; set; }
    }
}
