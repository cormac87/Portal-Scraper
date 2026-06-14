using System.Text;
using System.Text.RegularExpressions;
using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class RelevantPlanningDocumentService : IRelevantPlanningDocumentService
{
    private const int RelevantDocumentContextWordCount = 200;

    private static readonly Regex KeywordRegex = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public List<RelevantDocumentMatch> BuildMatches(
        IEnumerable<PlanningDocument> documents,
        IReadOnlyList<FullTextSearchCriterion> criteria)
    {
        var matches = new List<RelevantDocumentMatch>();

        foreach (var document in documents.OrderByDescending(document => document.PublishedDate).ThenBy(document => document.Name))
        {
            if (string.IsNullOrWhiteSpace(document.ContentText))
            {
                continue;
            }

            var documentWords = GetWordSpans(document.ContentText);
            if (documentWords.Count == 0)
            {
                continue;
            }

            var matchingWordRanges = criteria
                .SelectMany(criterion => GetCriterionWordMatchRanges(criterion, documentWords))
                .GroupBy(range => new { range.StartWordIndex, range.EndWordIndex })
                .Select(group => group.First())
                .ToList();

            if (matchingWordRanges.Count == 0)
            {
                continue;
            }

            var snippets = BuildRelevantDocumentSnippets(document.ContentText, documentWords, matchingWordRanges);
            if (snippets.Count == 0)
            {
                continue;
            }

            matches.Add(new RelevantDocumentMatch
            {
                Document = document,
                Snippets = snippets,
                MatchCount = snippets.Sum(snippet => snippet.MatchCount)
            });
        }

        return matches;
    }

    private static List<RelevantDocumentSnippet> BuildRelevantDocumentSnippets(
        string contentText,
        IReadOnlyList<TextWord> documentWords,
        IReadOnlyList<WordRange> matchingWordRanges)
    {
        if (matchingWordRanges.Count == 0)
        {
            return [];
        }

        var ranges = MergeWordRanges(matchingWordRanges
            .Select(range => new WordRange
            {
                StartWordIndex = Math.Max(0, range.StartWordIndex - RelevantDocumentContextWordCount),
                EndWordIndex = Math.Min(documentWords.Count - 1, range.EndWordIndex + RelevantDocumentContextWordCount)
            })
            .ToList());

        return ranges
            .Select(range =>
            {
                var highlightedHtml = HighlightMatchingWordRanges(contentText, documentWords, range, matchingWordRanges);

                if (range.StartWordIndex > 0)
                {
                    highlightedHtml = "... " + highlightedHtml;
                }

                if (range.EndWordIndex < documentWords.Count - 1)
                {
                    highlightedHtml += " ...";
                }

                return new RelevantDocumentSnippet
                {
                    HighlightedHtml = highlightedHtml,
                    MatchCount = CountMatchingWordRanges(range, matchingWordRanges)
                };
            })
            .ToList();
    }

    private static List<WordRange> MergeWordRanges(IReadOnlyList<WordRange> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges.ToList();
        }

        var orderedRanges = ranges
            .OrderBy(range => range.StartWordIndex)
            .ThenBy(range => range.EndWordIndex)
            .ToList();

        var mergedRanges = new List<WordRange>();
        var currentRange = orderedRanges[0];

        foreach (var range in orderedRanges.Skip(1))
        {
            if (range.StartWordIndex <= currentRange.EndWordIndex + 1)
            {
                currentRange.EndWordIndex = Math.Max(currentRange.EndWordIndex, range.EndWordIndex);
                continue;
            }

            mergedRanges.Add(currentRange);
            currentRange = range;
        }

        mergedRanges.Add(currentRange);

        return mergedRanges;
    }

    private static int CountMatchingWordRanges(WordRange snippetRange, IReadOnlyList<WordRange> matchingWordRanges)
    {
        return matchingWordRanges.Count(match =>
            match.StartWordIndex >= snippetRange.StartWordIndex &&
            match.EndWordIndex <= snippetRange.EndWordIndex);
    }

    private static List<WordRange> GetCriterionWordMatchRanges(
        FullTextSearchCriterion criterion,
        IReadOnlyList<TextWord> documentWords)
    {
        if (criterion.Alternatives.Count > 0)
        {
            return criterion.Alternatives
                .SelectMany(alternative => GetSearchTermWordMatchRanges(
                    alternative.SearchTerms,
                    criterion.RequireAdjacentWords,
                    documentWords))
                .ToList();
        }

        return GetSearchTermWordMatchRanges(
            criterion.SearchTerms,
            criterion.RequireAdjacentWords,
            documentWords);
    }

    private static List<WordRange> GetSearchTermWordMatchRanges(
        IReadOnlyList<string> searchTerms,
        bool requireAdjacentWords,
        IReadOnlyList<TextWord> documentWords)
    {
        if (requireAdjacentWords && searchTerms.Count > 1)
        {
            return FindAdjacentSearchTermRanges(searchTerms, documentWords);
        }

        if (!searchTerms.All(term => documentWords.Any(word => WordMatchesSearchTerm(word.Text, term))))
        {
            return [];
        }

        return documentWords
            .Select((word, index) => new { Word = word, Index = index })
            .Where(item => searchTerms.Any(term => WordMatchesSearchTerm(item.Word.Text, term)))
            .Select(item => new WordRange
            {
                StartWordIndex = item.Index,
                EndWordIndex = item.Index
            })
            .ToList();
    }

    private static List<WordRange> FindAdjacentSearchTermRanges(
        IReadOnlyList<string> searchTerms,
        IReadOnlyList<TextWord> documentWords)
    {
        if (searchTerms.Count == 0 || documentWords.Count < searchTerms.Count)
        {
            return [];
        }

        var ranges = new List<WordRange>();

        for (var startIndex = 0; startIndex <= documentWords.Count - searchTerms.Count; startIndex++)
        {
            var isMatch = true;

            for (var termIndex = 0; termIndex < searchTerms.Count; termIndex++)
            {
                if (WordMatchesSearchTerm(documentWords[startIndex + termIndex].Text, searchTerms[termIndex]))
                {
                    continue;
                }

                isMatch = false;
                break;
            }

            if (isMatch)
            {
                ranges.Add(new WordRange
                {
                    StartWordIndex = startIndex,
                    EndWordIndex = startIndex + searchTerms.Count - 1
                });
            }
        }

        return ranges;
    }

    private static List<TextWord> GetWordSpans(string text)
    {
        return KeywordRegex.Matches(text)
            .Select(match => new TextWord
            {
                Text = match.Value,
                Start = match.Index,
                End = match.Index + match.Length
            })
            .ToList();
    }

    private static bool WordMatchesSearchTerm(string word, string searchTerm)
    {
        return word.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase);
    }

    private static string HighlightMatchingWordRanges(
        string contentText,
        IReadOnlyList<TextWord> documentWords,
        WordRange snippetRange,
        IReadOnlyList<WordRange> matchingWordRanges)
    {
        var snippetStart = documentWords[snippetRange.StartWordIndex].Start;
        var snippetEnd = documentWords[snippetRange.EndWordIndex].End;
        var highlightSpans = matchingWordRanges
            .Where(range =>
                range.StartWordIndex >= snippetRange.StartWordIndex &&
                range.EndWordIndex <= snippetRange.EndWordIndex)
            .Select(range => new TextSpan
            {
                Start = documentWords[range.StartWordIndex].Start,
                End = documentWords[range.EndWordIndex].End
            })
            .ToList();

        return HighlightTextSpans(contentText, snippetStart, snippetEnd, highlightSpans);
    }

    private static string HighlightTextSpans(string text, int start, int end, IReadOnlyList<TextSpan> highlightSpans)
    {
        if (highlightSpans.Count == 0)
        {
            return System.Net.WebUtility.HtmlEncode(text[start..end]);
        }

        var highlightedText = new StringBuilder();
        var previousIndex = start;

        foreach (var span in MergeTextSpans(highlightSpans))
        {
            var spanStart = Math.Clamp(span.Start, start, end);
            var spanEnd = Math.Clamp(span.End, start, end);

            if (spanEnd <= previousIndex)
            {
                continue;
            }

            highlightedText.Append(System.Net.WebUtility.HtmlEncode(text[previousIndex..spanStart]));
            highlightedText.Append("<mark>");
            highlightedText.Append(System.Net.WebUtility.HtmlEncode(text[spanStart..spanEnd]));
            highlightedText.Append("</mark>");
            previousIndex = spanEnd;
        }

        highlightedText.Append(System.Net.WebUtility.HtmlEncode(text[previousIndex..end]));

        return highlightedText.ToString();
    }

    private static List<TextSpan> MergeTextSpans(IReadOnlyList<TextSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans.ToList();
        }

        var orderedSpans = spans
            .OrderBy(span => span.Start)
            .ThenBy(span => span.End)
            .ToList();
        var mergedSpans = new List<TextSpan>();
        var currentSpan = orderedSpans[0];

        foreach (var span in orderedSpans.Skip(1))
        {
            if (span.Start <= currentSpan.End)
            {
                currentSpan.End = Math.Max(currentSpan.End, span.End);
                continue;
            }

            mergedSpans.Add(currentSpan);
            currentSpan = span;
        }

        mergedSpans.Add(currentSpan);

        return mergedSpans;
    }

    private sealed class TextWord
    {
        public string Text { get; set; } = string.Empty;

        public int Start { get; set; }

        public int End { get; set; }
    }

    private sealed class TextSpan
    {
        public int Start { get; set; }

        public int End { get; set; }
    }

    private sealed class WordRange
    {
        public int StartWordIndex { get; set; }

        public int EndWordIndex { get; set; }
    }
}
