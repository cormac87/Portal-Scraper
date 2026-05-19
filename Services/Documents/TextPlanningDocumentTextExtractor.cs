using System.Text;

namespace PortalScraper.Services.Documents;

public sealed class TextPlanningDocumentTextExtractor : PlanningDocumentTextExtractor
{
    public override string Name => "Text";

    public override IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".csv",
            ".tsv",
            ".txt"
        };

    public override IReadOnlySet<string> SupportedContentTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "text/csv",
            "text/plain",
            "text/tab-separated-values"
        };

    public override async Task<string> ExtractTextAsync(
        DownloadedPlanningDocument document,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(document.Content);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(cancellationToken)).Trim();
    }
}
