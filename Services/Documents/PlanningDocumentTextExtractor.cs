namespace PortalScraper.Services.Documents;

public abstract class PlanningDocumentTextExtractor
{
    public abstract string Name { get; }

    public abstract IReadOnlySet<string> SupportedExtensions { get; }

    public virtual IReadOnlySet<string> SupportedContentTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public virtual bool CanExtract(DownloadedPlanningDocument document)
    {
        var extension = Path.GetExtension(document.FileName);
        if (!string.IsNullOrWhiteSpace(extension)
            && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(document.ContentType)
            && SupportedContentTypes.Contains(document.ContentType, StringComparer.OrdinalIgnoreCase);
    }

    public abstract Task<string> ExtractTextAsync(
        DownloadedPlanningDocument document,
        CancellationToken cancellationToken);
}
