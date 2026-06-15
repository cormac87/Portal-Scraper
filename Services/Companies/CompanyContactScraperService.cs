using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

public sealed class CompanyContactScraperService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<CompanyContactScraperService> logger) : ICompanyContactScraperService
{
    private const int DefaultTimeoutMilliseconds = 15_000;
    private const int DefaultNavigationTimeoutMilliseconds = 20_000;
    private const int MaxPagesPerCompany = 8;
    private const int MaxMessages = 100;

    private static readonly Regex EmailRegex = new(
        @"(?<![A-Z0-9._%+\-])([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})(?![A-Z0-9._%+\-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TokenRegex = new(
        @"[a-z0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> CompanyTokenStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and",
        "co",
        "company",
        "group",
        "holdings",
        "inc",
        "limited",
        "llc",
        "llp",
        "ltd",
        "plc",
        "services",
        "the",
        "uk"
    };

    private static readonly HashSet<string> ExcludedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "bing.com",
        "business.data.gov.uk",
        "checkcompany.co.uk",
        "company-information.service.gov.uk",
        "companycheck.co.uk",
        "companiesintheuk.co.uk",
        "companieslist.co.uk",
        "companiesintheuk.com",
        "companieshouse.gov.uk",
        "crunchbase.com",
        "endole.co.uk",
        "facebook.com",
        "find-and-update.company-information.service.gov.uk",
        "gov.uk",
        "google.com",
        "instagram.com",
        "kompass.com",
        "linkedin.com",
        "opencorporates.com",
        "suite.endole.co.uk",
        "thegazette.co.uk",
        "twitter.com",
        "ukbizdb.co.uk",
        "yell.com",
        "yelp.com",
        "zoominfo.com"
    };

    private static readonly HashSet<string> GenericEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "aol.com",
        "gmail.com",
        "googlemail.com",
        "hotmail.co.uk",
        "hotmail.com",
        "icloud.com",
        "live.co.uk",
        "live.com",
        "mail.com",
        "me.com",
        "outlook.com",
        "proton.me",
        "protonmail.com",
        "yahoo.co.uk",
        "yahoo.com"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<CompanyContactScrapeResult> ScrapeContactsAsync(
        CompanySearchFilters filters,
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var messages = new List<string>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var baseQuery = CompanyQuery.ApplyFilters(db.Companies.AsNoTracking(), filters);
        var candidateCompanies = await baseQuery.CountAsync(cancellationToken);
        var candidates = await CompanyQuery.ApplyDefaultSort(baseQuery
                .Where(company =>
                    company.CompanyName != null
                    && company.CompanyName != string.Empty
                    && (company.Email == null || company.Email == string.Empty)))
            .Select(company => new CompanyContactCandidate(
                company.Id,
                company.CompanyNumber,
                company.CompanyName!))
            .ToListAsync(cancellationToken);

        var skippedCompanies = candidateCompanies - candidates.Count;
        var searchedCompanies = 0;
        var emailsFound = 0;
        var emailsUpdated = 0;
        var failedCompanies = 0;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125 Safari/537.36"
        });

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            searchedCompanies++;

            try
            {
                var match = await ScrapeCompanyContactAsync(context, candidate, cancellationToken);
                if (match is null)
                {
                    AddMessage(messages, $"No trusted email found for {candidate.CompanyName}.");
                    continue;
                }

                emailsFound++;
                var updated = await UpdateCompanyEmailAsync(candidate.Id, match.Email, cancellationToken);
                if (updated)
                {
                    emailsUpdated++;
                    AddMessage(messages, $"Found {match.Email} for {candidate.CompanyName} from {match.SourceUrl.Host}.");
                }
                else
                {
                    AddMessage(messages, $"Found {match.Email} for {candidate.CompanyName}, but the company already had an email.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCompanies++;
                logger.LogWarning(ex, "Unable to scrape contacts for company {CompanyNumber} {CompanyName}", candidate.CompanyNumber, candidate.CompanyName);
                AddMessage(messages, $"Failed {candidate.CompanyName}: {ex.Message}");
            }
        }

        return new CompanyContactScrapeResult(
            startedAtUtc,
            DateTime.UtcNow,
            candidateCompanies,
            searchedCompanies,
            skippedCompanies,
            emailsFound,
            emailsUpdated,
            failedCompanies,
            messages);
    }

    private async Task<CompanyContactMatch?> ScrapeCompanyContactAsync(
        IBrowserContext context,
        CompanyContactCandidate candidate,
        CancellationToken cancellationToken)
    {
        var page = await context.NewPageAsync();
        try
        {
            page.SetDefaultTimeout(DefaultTimeoutMilliseconds);
            page.SetDefaultNavigationTimeout(DefaultNavigationTimeoutMilliseconds);

            var searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString($"\"{candidate.CompanyName}\" official website")}";
            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = DefaultNavigationTimeoutMilliseconds
            });

            var websiteUrl = (await ExtractGoogleResultUrlsAsync(page, cancellationToken))
                .FirstOrDefault(url => IsTrustedCompanyWebsite(url, candidate.CompanyName));
            if (websiteUrl is null)
            {
                return null;
            }

            return await ScrapeWebsiteEmailsAsync(page, websiteUrl, candidate.CompanyName, cancellationToken);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task<IReadOnlyList<Uri>> ExtractGoogleResultUrlsAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hrefsJson = await page.Locator("a[href]").EvaluateAllAsync<string>(
            "anchors => JSON.stringify(anchors.map(anchor => anchor.href).filter(Boolean))");
        var hrefs = JsonSerializer.Deserialize<List<string>>(hrefsJson, JsonOptions) ?? [];
        var results = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var href in hrefs)
        {
            var resultUrl = NormalizeSearchResultUrl(href);
            if (resultUrl is null || !seen.Add(resultUrl.AbsoluteUri))
            {
                continue;
            }

            results.Add(resultUrl);
        }

        return results;
    }

    private static Uri? NormalizeSearchResultUrl(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (IsGoogleHost(uri.Host))
        {
            var nestedUrl = GetQueryParameter(uri.Query, "q")
                ?? GetQueryParameter(uri.Query, "url");
            if (string.IsNullOrWhiteSpace(nestedUrl)
                || !Uri.TryCreate(nestedUrl, UriKind.Absolute, out uri))
            {
                return null;
            }
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return LooksLikeDocumentUrl(uri) ? null : uri;
    }

    private async Task<CompanyContactMatch?> ScrapeWebsiteEmailsAsync(
        IPage page,
        Uri websiteUrl,
        string companyName,
        CancellationToken cancellationToken)
    {
        var urls = new List<Uri> { websiteUrl };
        urls.AddRange(BuildLikelyContactUrls(websiteUrl));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < urls.Count && index < MaxPagesPerCompany; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageUrl = urls[index];
            if (!seen.Add(pageUrl.AbsoluteUri) || !IsSameHost(websiteUrl, pageUrl))
            {
                continue;
            }

            IResponse? response;
            try
            {
                response = await page.GotoAsync(pageUrl.ToString(), new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = DefaultNavigationTimeoutMilliseconds
                });
            }
            catch (PlaywrightException ex)
            {
                logger.LogDebug(ex, "Unable to load potential contact page {ContactPageUrl}", pageUrl);
                continue;
            }

            if (response is { Status: >= 400 })
            {
                continue;
            }

            var html = await page.ContentAsync();
            var email = ExtractTrustedEmails(html, websiteUrl, companyName).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(email))
            {
                return new CompanyContactMatch(email, websiteUrl, pageUrl);
            }

            if (index == 0)
            {
                urls.AddRange(await ExtractContactLinksAsync(page, websiteUrl, cancellationToken));
            }
        }

        return null;
    }

    private static IEnumerable<Uri> BuildLikelyContactUrls(Uri websiteUrl)
    {
        var root = new Uri($"{websiteUrl.Scheme}://{websiteUrl.Host}");
        var paths = new[]
        {
            "/contact",
            "/contact/",
            "/contact-us",
            "/contact-us/",
            "/contacts",
            "/contacts/",
            "/about",
            "/about/",
            "/about-us",
            "/about-us/"
        };

        return paths.Select(path => new Uri(root, path));
    }

    private static async Task<IReadOnlyList<Uri>> ExtractContactLinksAsync(
        IPage page,
        Uri websiteUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var anchorsJson = await page.Locator("a[href]").EvaluateAllAsync<string>(
            @"anchors => JSON.stringify(anchors.map(anchor => ({
                href: anchor.href,
                text: anchor.innerText || anchor.getAttribute('aria-label') || ''
            })).filter(anchor => anchor.href))");
        var anchors = JsonSerializer.Deserialize<List<ContactAnchor>>(anchorsJson, JsonOptions) ?? [];
        var links = new List<Uri>();

        foreach (var anchor in anchors)
        {
            if (links.Count >= MaxPagesPerCompany)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(anchor.Href)
                || !Uri.TryCreate(anchor.Href, UriKind.Absolute, out var uri)
                || !IsSameHost(websiteUrl, uri)
                || LooksLikeDocumentUrl(uri))
            {
                continue;
            }

            var combinedText = $"{anchor.Text} {uri.AbsolutePath}";
            if (combinedText.Contains("contact", StringComparison.OrdinalIgnoreCase)
                || combinedText.Contains("about", StringComparison.OrdinalIgnoreCase)
                || combinedText.Contains("enquir", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(uri);
            }
        }

        return links;
    }

    private static IEnumerable<string> ExtractTrustedEmails(
        string html,
        Uri websiteUrl,
        string companyName)
    {
        var decodedHtml = WebUtility.HtmlDecode(html);
        return EmailRegex
            .Matches(decodedHtml)
            .Select(match => NormalizeEmail(match.Groups[1].Value))
            .Where(email => email is not null)
            .Select(email => email!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(email => IsTrustedEmail(email, websiteUrl, companyName))
            .OrderBy(email => IsRoleEmail(email) ? 0 : 1)
            .ThenBy(email => email, StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizeEmail(string email)
    {
        var normalized = email
            .Trim()
            .Trim('.', ',', ';', ':', ')', ']', '}', '"', '\'')
            .ToLowerInvariant();

        if (normalized.EndsWith(".png", StringComparison.Ordinal)
            || normalized.EndsWith(".jpg", StringComparison.Ordinal)
            || normalized.EndsWith(".jpeg", StringComparison.Ordinal)
            || normalized.EndsWith(".gif", StringComparison.Ordinal)
            || normalized.EndsWith(".svg", StringComparison.Ordinal))
        {
            return null;
        }

        return normalized;
    }

    private static bool IsTrustedCompanyWebsite(Uri uri, string companyName)
    {
        return !IsExcludedHost(uri.Host)
            && IsHostSimilarToCompany(uri.Host, companyName);
    }

    private static bool IsTrustedEmail(string email, Uri websiteUrl, string companyName)
    {
        var atIndex = email.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == email.Length - 1)
        {
            return false;
        }

        var emailHost = email[(atIndex + 1)..];
        if (GenericEmailDomains.Contains(emailHost) || IsExcludedHost(emailHost))
        {
            return false;
        }

        var emailCore = GetDomainCore(emailHost);
        var websiteCore = GetDomainCore(websiteUrl.Host);
        if (!string.IsNullOrWhiteSpace(emailCore)
            && !string.IsNullOrWhiteSpace(websiteCore)
            && (emailCore.Equals(websiteCore, StringComparison.OrdinalIgnoreCase)
                || emailCore.Contains(websiteCore, StringComparison.OrdinalIgnoreCase)
                || websiteCore.Contains(emailCore, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IsHostSimilarToCompany(emailHost, companyName);
    }

    private static bool IsHostSimilarToCompany(string host, string companyName)
    {
        var domainCore = GetDomainCore(host);
        if (string.IsNullOrWhiteSpace(domainCore))
        {
            return false;
        }

        var companyTokens = GetCompanyTokens(companyName);
        if (companyTokens.Count == 0)
        {
            return false;
        }

        var compactName = string.Concat(companyTokens);
        if (compactName.Length >= 5
            && (domainCore.Contains(compactName, StringComparison.OrdinalIgnoreCase)
                || compactName.Contains(domainCore, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return companyTokens.Any(token =>
            token.Length >= 4 && domainCore.Contains(token, StringComparison.OrdinalIgnoreCase)
            || token.Length == 3 && domainCore.StartsWith(token, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetCompanyTokens(string companyName)
    {
        return TokenRegex
            .Matches(companyName.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length >= 3 && !CompanyTokenStopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetDomainCore(string host)
    {
        var normalized = host.Trim().Trim('.').ToLowerInvariant();
        if (normalized.StartsWith("www.", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }

        var parts = normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        if (parts.Count >= 3 && parts[^1].Length == 2 && parts[^2].Length <= 3)
        {
            return NormalizeDomainPart(parts[^3]);
        }

        return parts.Count >= 2
            ? NormalizeDomainPart(parts[^2])
            : NormalizeDomainPart(parts[0]);
    }

    private static string NormalizeDomainPart(string value)
    {
        return string.Concat(TokenRegex
            .Matches(value)
            .Select(match => match.Value));
    }

    private static bool IsExcludedHost(string host)
    {
        var normalized = host.Trim().Trim('.').ToLowerInvariant();
        if (normalized.StartsWith("www.", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }

        return ExcludedHosts.Any(excludedHost =>
            normalized.Equals(excludedHost, StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith($".{excludedHost}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGoogleHost(string host)
    {
        var normalized = host.Trim().Trim('.').ToLowerInvariant();
        return normalized.Equals("google.com", StringComparison.Ordinal)
            || normalized.EndsWith(".google.com", StringComparison.Ordinal)
            || normalized.Equals("google.co.uk", StringComparison.Ordinal)
            || normalized.EndsWith(".google.co.uk", StringComparison.Ordinal);
    }

    private static bool IsSameHost(Uri first, Uri second)
    {
        return first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase)
            || first.Host.EndsWith($".{second.Host}", StringComparison.OrdinalIgnoreCase)
            || second.Host.EndsWith($".{first.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDocumentUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetQueryParameter(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmedQuery = query[0] == '?' ? query[1..] : query;
        foreach (var part in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
            var key = equalsIndex >= 0 ? part[..equalsIndex] : part;
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = equalsIndex >= 0 ? part[(equalsIndex + 1)..] : string.Empty;
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }

    private async Task<bool> UpdateCompanyEmailAsync(
        Guid companyId,
        string email,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.Companies
            .Where(company => company.Id == companyId && (company.Email == null || company.Email == string.Empty))
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(company => company.Email, email),
                cancellationToken);

        return updated > 0;
    }

    private static bool IsRoleEmail(string email)
    {
        var localPart = email.Split('@')[0];
        return localPart is "admin" or "contact" or "enquiries" or "enquiry" or "hello" or "info" or "office" or "sales";
    }

    private static void AddMessage(List<string> messages, string message)
    {
        if (messages.Count >= MaxMessages)
        {
            return;
        }

        messages.Add(message);
    }

    private sealed record CompanyContactCandidate(
        Guid Id,
        string CompanyNumber,
        string CompanyName);

    private sealed record CompanyContactMatch(
        string Email,
        Uri WebsiteUrl,
        Uri SourceUrl);

    private sealed class ContactAnchor
    {
        public string? Href { get; set; }

        public string? Text { get; set; }
    }
}
