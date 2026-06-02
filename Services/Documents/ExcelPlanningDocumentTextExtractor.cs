using System.Text;
using ExcelDataReader;

namespace PortalScraper.Services.Documents;

public sealed class ExcelPlanningDocumentTextExtractor : PlanningDocumentTextExtractor
{
    public override string Name => "Excel";

    public override IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xls",
            ".xlsm",
            ".xlsx"
        };

    public override IReadOnlySet<string> SupportedContentTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/vnd.ms-excel",
            "application/vnd.ms-excel.sheet.macroEnabled.12",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

    public override Task<string> ExtractTextAsync(
        DownloadedPlanningDocument document,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(document.Content);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var builder = new StringBuilder();

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            builder.AppendLine($"Sheet: {reader.Name}");

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cells = new List<string>();
                for (var fieldIndex = 0; fieldIndex < reader.FieldCount; fieldIndex++)
                {
                    cells.Add(Convert.ToString(reader.GetValue(fieldIndex)) ?? string.Empty);
                }

                if (cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                {
                    builder.AppendLine(string.Join('\t', cells));
                }
            }

            builder.AppendLine();
        } while (reader.NextResult());

        return Task.FromResult(builder.ToString().Trim());
    }
}
