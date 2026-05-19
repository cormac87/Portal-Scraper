using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NPOI.HWPF;
using NPOI.HWPF.Extractor;

namespace PortalScraper.Services.Documents;

public sealed class WordPlanningDocumentTextExtractor : PlanningDocumentTextExtractor
{
    public override string Name => "Word";

    public override IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".doc",
            ".docx"
        };

    public override IReadOnlySet<string> SupportedContentTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };

    public override Task<string> ExtractTextAsync(
        DownloadedPlanningDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream(document.Content);
        if (Path.GetExtension(document.FileName).Equals(".doc", StringComparison.OrdinalIgnoreCase))
        {
            var legacyDocument = new HWPFDocument(stream);
            var legacyExtractor = new WordExtractor(legacyDocument);
            return Task.FromResult(legacyExtractor.Text.Trim());
        }

        using var wordDocument = WordprocessingDocument.Open(stream, false);
        var body = wordDocument.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return Task.FromResult(string.Empty);
        }

        var builder = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = string.Concat(paragraph.Descendants<Text>().Select(node => node.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine(text);
            }
        }

        return Task.FromResult(builder.ToString().Trim());
    }
}
