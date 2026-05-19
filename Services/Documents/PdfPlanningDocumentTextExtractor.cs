using System.Text;
using UglyToad.PdfPig;

namespace PortalScraper.Services.Documents;

public sealed class PdfPlanningDocumentTextExtractor : PlanningDocumentTextExtractor
{
    public override string Name => "PDF";

    public override IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public override IReadOnlySet<string> SupportedContentTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf" };

    public override Task<string> ExtractTextAsync(
        DownloadedPlanningDocument document,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(document.Content);
        using var pdf = PdfDocument.Open(stream);
        var builder = new StringBuilder();

        foreach (var page in pdf.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendLine(page.Text);
            builder.AppendLine();
        }

        return Task.FromResult(builder.ToString().Trim());
    }
}
