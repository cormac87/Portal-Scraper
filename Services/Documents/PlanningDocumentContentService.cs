using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Playwright;
using PortalScraper.Services;

namespace PortalScraper.Services.Documents;

public interface IPlanningDocumentContentService
{
    Task<PlanningDocumentTextExtractionResult> ExtractAsync(
        string url,
        IBrowserContext browserContext,
        CancellationToken cancellationToken);
}

public sealed class PlanningDocumentContentService(
    IEnumerable<PlanningDocumentTextExtractor> extractors,
    ILogger<PlanningDocumentContentService> logger) : IPlanningDocumentContentService
{
    private const int MaxDownloadBytes = 40 * 1024 * 1024;

    public async Task<PlanningDocumentTextExtractionResult> ExtractAsync(
        string url,
        IBrowserContext browserContext,
        CancellationToken cancellationToken)
    {
        var extractionTimer = Stopwatch.StartNew();
        DownloadedPlanningDocument document;

        logger.LogInformation("Starting text extraction for planning document {DocumentUrl}", url);

        try
        {
            logger.LogDebug("Downloading planning document {DocumentUrl}", url);
            document = await DownloadAsync(url, browserContext, cancellationToken);
            logger.LogInformation(
                "Downloaded planning document {DocumentFileName} from {DocumentUrl}. ContentType={ContentType}, SizeBytes={SizeBytes}",
                document.FileName,
                document.Url,
                document.ContentType ?? "unknown",
                document.Content.Length);
        }
        catch (Exception ex)
            when (PlaywrightBrowserFailure.IsRestartable(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to download planning document {DocumentUrl} after {ElapsedMilliseconds} ms",
                url,
                extractionTimer.ElapsedMilliseconds);

            return new PlanningDocumentTextExtractionResult(
                null,
                GetFileNameFromUrl(url),
                null,
                "DownloadFailed",
                ex.Message);
        }

        var extractor = extractors.FirstOrDefault(extractor => extractor.CanExtract(document));
        if (extractor is null)
        {
            logger.LogInformation(
                "No text extractor found for planning document {DocumentFileName}. ContentType={ContentType}, Extension={Extension}",
                document.FileName,
                document.ContentType ?? "unknown",
                Path.GetExtension(document.FileName));

            return new PlanningDocumentTextExtractionResult(
                null,
                document.FileName,
                document.ContentType,
                "Unsupported",
                $"No text extractor is registered for '{Path.GetExtension(document.FileName)}'.");
        }

        try
        {
            logger.LogInformation(
                "Extracting text from planning document {DocumentFileName} using {ExtractorName} extractor",
                document.FileName,
                extractor.Name);

            var text = await extractor.ExtractTextAsync(document, cancellationToken);
            var characterCount = string.IsNullOrWhiteSpace(text) ? 0 : text.Length;

            logger.LogInformation(
                "Parsed planning document {DocumentFileName} using {ExtractorName} extractor in {ElapsedMilliseconds} ms. CharactersExtracted={CharacterCount}",
                document.FileName,
                extractor.Name,
                extractionTimer.ElapsedMilliseconds,
                characterCount);

            return new PlanningDocumentTextExtractionResult(
                string.IsNullOrWhiteSpace(text) ? null : text,
                document.FileName,
                document.ContentType,
                "Parsed",
                null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to parse planning document {DocumentUrl} with {ExtractorName} extractor after {ElapsedMilliseconds} ms",
                url,
                extractor.Name,
                extractionTimer.ElapsedMilliseconds);

            return new PlanningDocumentTextExtractionResult(
                null,
                document.FileName,
                document.ContentType,
                "ParseFailed",
                ex.Message);
        }
    }

    private static async Task<DownloadedPlanningDocument> DownloadAsync(
        string url,
        IBrowserContext browserContext,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url, UriKind.Absolute);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = await BuildCookieContainerAsync(browserContext, uri),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 PortalScraper/1.0");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"Document is too large to parse ({contentLength.Value:N0} bytes).");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        await responseStream.CopyToAsync(memoryStream, cancellationToken);

        if (memoryStream.Length > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"Document is too large to parse ({memoryStream.Length:N0} bytes).");
        }

        var fileName = GetFileName(response.Content.Headers.ContentDisposition, uri);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        return new DownloadedPlanningDocument(
            uri.ToString(),
            fileName,
            contentType,
            memoryStream.ToArray());
    }

    private static async Task<CookieContainer> BuildCookieContainerAsync(
        IBrowserContext browserContext,
        Uri uri)
    {
        var cookieContainer = new CookieContainer();
        var cookies = await browserContext.CookiesAsync(new[] { uri.ToString() });

        foreach (var browserCookie in cookies)
        {
            var domain = browserCookie.Domain.TrimStart('.');
            var path = string.IsNullOrWhiteSpace(browserCookie.Path) ? "/" : browserCookie.Path;

            try
            {
                cookieContainer.Add(
                    uri,
                    new System.Net.Cookie(browserCookie.Name, browserCookie.Value, path, domain)
                    {
                        Secure = browserCookie.Secure,
                        HttpOnly = browserCookie.HttpOnly
                    });
            }
            catch (CookieException)
            {
                cookieContainer.Add(uri, new System.Net.Cookie(browserCookie.Name, browserCookie.Value));
            }
        }

        return cookieContainer;
    }

    private static string GetFileName(ContentDispositionHeaderValue? contentDisposition, Uri uri)
    {
        var headerFileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(headerFileName))
        {
            return headerFileName.Trim('"');
        }

        return GetFileNameFromUrl(uri.ToString());
    }

    private static string GetFileNameFromUrl(string url)
    {
        var path = new Uri(url, UriKind.Absolute).AbsolutePath;
        var fileName = Path.GetFileName(WebUtility.UrlDecode(path));
        return string.IsNullOrWhiteSpace(fileName) ? "document" : fileName;
    }
}
