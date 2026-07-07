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
    private const int MaxSearchResultsPerCompany = 6;
    private const int MaxMessages = 500;
    private const int MaxDiagnosticsPerCompany = 80;
    private const int ReputablePageScoreThreshold = 45;
    private const int TrustedEmailScoreThreshold = 75;
    private const int StrongEmailScoreThreshold = 135;

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
        if (CompanyQuery.RequiresFullTextSearch(filters)
            && !await CompanyQuery.IsFullTextSearchAvailableAsync(db, cancellationToken))
        {
            throw new InvalidOperationException("Company contact scraping requires SQL Server Full-Text Search and the company full-text index when filtering by company name.");
        }

        var baseQuery = CompanyQuery.CreateSearchQuery(db, filters);
        var candidateCompanies = await baseQuery.CountAsync(cancellationToken);
        var candidates = await CompanyQuery.ApplyDefaultSort(baseQuery
                .Where(result =>
                    result.CompanyName != null
                    && result.CompanyName != string.Empty
                    && (result.Email == null || result.Email == string.Empty)),
                filters)
            .Select(result => new CompanyContactCandidate(
                result.CompanyId,
                result.CompanyNumber,
                result.CompanyName!))
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
            var diagnostics = new List<string>();

            try
            {
                var match = await ScrapeCompanyContactAsync(context, candidate, diagnostics, cancellationToken);
                if (diagnostics.Count > 0)
                {
                    logger.LogInformation(
                        "Contact scrape diagnostics for {CompanyNumber} {CompanyName}: {Diagnostics}",
                        candidate.CompanyNumber,
                        candidate.CompanyName,
                        string.Join(" | ", diagnostics));
                }

                if (match is null)
                {
                    AddMessage(messages, $"No trusted email found for {candidate.CompanyName}.");
                    AddDiagnosticMessages(messages, candidate, diagnostics);
                    continue;
                }

                emailsFound++;
                var updated = await UpdateCompanyEmailAsync(candidate.Id, match.Email, cancellationToken);
                if (updated)
                {
                    emailsUpdated++;
                    AddMessage(messages, $"Found {match.Email} for {candidate.CompanyName} from {match.SourceUrl.Host} (score {match.Score}).");
                }
                else
                {
                    AddMessage(messages, $"Found {match.Email} for {candidate.CompanyName} (score {match.Score}), but the company already had an email.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCompanies++;
                logger.LogWarning(ex, "Unable to scrape contacts for company {CompanyNumber} {CompanyName}", candidate.CompanyNumber, candidate.CompanyName);
                AddMessage(messages, $"Failed {candidate.CompanyName}: {ex.Message}");
                AddDiagnosticMessages(messages, candidate, diagnostics);
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
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var page = await context.NewPageAsync();
        try
        {
            page.SetDefaultTimeout(DefaultTimeoutMilliseconds);
            page.SetDefaultNavigationTimeout(DefaultNavigationTimeoutMilliseconds);

            var searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString($"\"{candidate.CompanyName}\" official website")}";
            AddDiagnostic(diagnostics, $"Searching Google: {searchUrl}");
            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = DefaultNavigationTimeoutMilliseconds
            });

            var searchResults = await ExtractGoogleResultUrlsAsync(page, cancellationToken);
            AddDiagnostic(diagnostics, $"Google yielded {searchResults.Count} unique absolute URL(s): {DescribeUris(searchResults, 8)}");

            var candidateUrls = MergeCandidateWebsiteUrls(searchResults, BuildCompanyWebsiteGuesses(candidate.CompanyName));
            AddDiagnostic(diagnostics, $"Candidate URL list after domain guesses: {DescribeUris(candidateUrls, 10)}");

            var websiteUrls = candidateUrls
                .Where(url =>
                {
                    var rejectionReason = GetPotentialCompanyWebsiteRejectionReason(url);
                    if (rejectionReason is null)
                    {
                        return true;
                    }

                    AddDiagnostic(diagnostics, $"Skipping {url}: {rejectionReason}");
                    return false;
                })
                .Select((url, index) => new
                {
                    Url = url,
                    Index = index,
                    HostSimilarityScore = ScoreHostSimilarityToCompany(url.Host, candidate.CompanyName)
                })
                .OrderByDescending(item => item.HostSimilarityScore)
                .ThenBy(item => item.Index)
                .Take(MaxSearchResultsPerCompany)
                .Select(item => item.Url)
                .ToList();
            AddDiagnostic(diagnostics, $"Trying {websiteUrls.Count} website candidate(s): {DescribeUris(websiteUrls, MaxSearchResultsPerCompany)}");
            if (websiteUrls.Count == 0)
            {
                return null;
            }

            CompanyContactMatch? bestMatch = null;
            foreach (var websiteUrl in websiteUrls)
            {
                var match = await ScrapeWebsiteEmailsAsync(page, websiteUrl, candidate.CompanyName, diagnostics, cancellationToken);
                if (match is null)
                {
                    AddDiagnostic(diagnostics, $"No accepted email found while trying {websiteUrl}.");
                    continue;
                }

                if (bestMatch is null || match.Score > bestMatch.Score)
                {
                    bestMatch = match;
                    AddDiagnostic(diagnostics, $"Best email so far: {match.Email} from {match.SourceUrl} (score {match.Score}).");
                }

                if (bestMatch.Score >= StrongEmailScoreThreshold)
                {
                    break;
                }
            }

            return bestMatch;
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
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var urls = new List<Uri> { websiteUrl };
        urls.AddRange(BuildLikelyContactUrls(websiteUrl));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hostSimilarityScore = ScoreHostSimilarityToCompany(websiteUrl.Host, companyName);
        var siteEvidence = CompanyPageEvidence.Empty;
        CompanyContactMatch? bestMatch = null;
        var bestScore = 0;

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
                AddDiagnostic(diagnostics, $"Loading page {pageUrl}.");
                response = await page.GotoAsync(pageUrl.ToString(), new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = DefaultNavigationTimeoutMilliseconds
                });
            }
            catch (PlaywrightException ex)
            {
                logger.LogDebug(ex, "Unable to load potential contact page {ContactPageUrl}", pageUrl);
                AddDiagnostic(diagnostics, $"Failed loading {pageUrl}: {ex.Message}");
                continue;
            }

            if (response is { Status: >= 400 })
            {
                AddDiagnostic(diagnostics, $"Skipping {pageUrl}: HTTP {response.Status}.");
                continue;
            }

            var html = await page.ContentAsync();
            siteEvidence = siteEvidence.BetterOf(ScoreCompanyPage(html, companyName));
            AddDiagnostic(
                diagnostics,
                $"Loaded {pageUrl}: html length {html.Length:N0}, host score {hostSimilarityScore}, page score {siteEvidence.Score}, reputable={siteEvidence.IsReputable}, evidence={siteEvidence.Summary}.");

            var scoredEmail = ExtractTrustedEmails(html, websiteUrl, companyName, siteEvidence, hostSimilarityScore, diagnostics).FirstOrDefault();
            if (scoredEmail is not null && scoredEmail.Score > bestScore)
            {
                bestScore = scoredEmail.Score;
                bestMatch = new CompanyContactMatch(scoredEmail.Email, websiteUrl, pageUrl, scoredEmail.Score);
            }

            if (index == 0)
            {
                urls.AddRange(await ExtractContactLinksAsync(page, websiteUrl, cancellationToken));
            }

            if (bestScore >= StrongEmailScoreThreshold)
            {
                break;
            }
        }

        return bestMatch;
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

    private static IEnumerable<Uri> BuildCompanyWebsiteGuesses(string companyName)
    {
        var compactName = string.Concat(GetCompanyTokens(companyName));
        if (compactName.Length < 5)
        {
            yield break;
        }

        var hosts = new[]
        {
            $"{compactName}.co.uk",
            $"www.{compactName}.co.uk",
            $"{compactName}.com",
            $"www.{compactName}.com"
        };

        foreach (var host in hosts)
        {
            if (Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var uri))
            {
                yield return uri;
            }
        }
    }

    private static List<Uri> MergeCandidateWebsiteUrls(params IEnumerable<Uri>[] urlGroups)
    {
        var urls = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var url in urlGroups.SelectMany(group => group))
        {
            if (seen.Add(url.AbsoluteUri))
            {
                urls.Add(url);
            }
        }

        return urls;
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

    private static IEnumerable<ScoredEmail> ExtractTrustedEmails(
        string html,
        Uri websiteUrl,
        string companyName,
        CompanyPageEvidence siteEvidence,
        int hostSimilarityScore,
        List<string> diagnostics)
    {
        var decodedHtml = WebUtility.HtmlDecode(html);
        var emails = EmailRegex
            .Matches(decodedHtml)
            .Select(match => NormalizeEmail(match.Groups[1].Value))
            .Where(email => email is not null)
            .Select(email => email!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (emails.Count == 0)
        {
            AddDiagnostic(diagnostics, "No email-shaped text found on this page.");
            return [];
        }

        AddDiagnostic(diagnostics, $"Email-shaped candidate(s): {string.Join(", ", emails.Take(12))}{(emails.Count > 12 ? ", ..." : string.Empty)}");

        var acceptedEmails = new List<ScoredEmail>();
        foreach (var email in emails)
        {
            var scoredEmail = ScoreEmail(email, decodedHtml, websiteUrl, companyName, siteEvidence, hostSimilarityScore, out var reason);
            if (scoredEmail is null)
            {
                AddDiagnostic(diagnostics, $"Rejected {email}: {reason}");
                continue;
            }

            if (scoredEmail.Score < TrustedEmailScoreThreshold)
            {
                AddDiagnostic(diagnostics, $"Rejected {email}: score {scoredEmail.Score} below {TrustedEmailScoreThreshold}; {reason}");
                continue;
            }

            AddDiagnostic(diagnostics, $"Accepted {email}: score {scoredEmail.Score}; {reason}");
            acceptedEmails.Add(scoredEmail);
        }

        return acceptedEmails
            .OrderByDescending(email => email.Score)
            .ThenBy(email => IsRoleEmail(email.Email) ? 0 : 1)
            .ThenBy(email => email.Email, StringComparer.OrdinalIgnoreCase);
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

    private static bool IsPotentialCompanyWebsite(Uri uri)
    {
        return GetPotentialCompanyWebsiteRejectionReason(uri) is null;
    }

    private static string? GetPotentialCompanyWebsiteRejectionReason(Uri uri)
    {
        if (IsExcludedHost(uri.Host))
        {
            return "host is excluded";
        }

        if (GenericEmailDomains.Contains(uri.Host))
        {
            return "host is a generic email provider";
        }

        if (LooksLikeDocumentUrl(uri))
        {
            return "URL looks like a document";
        }

        return null;
    }

    private static ScoredEmail? ScoreEmail(
        string email,
        string decodedHtml,
        Uri websiteUrl,
        string companyName,
        CompanyPageEvidence siteEvidence,
        int hostSimilarityScore,
        out string reason)
    {
        var atIndex = email.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == email.Length - 1)
        {
            reason = "email did not contain a valid local part and host";
            return null;
        }

        var emailHost = email[(atIndex + 1)..];
        if (GenericEmailDomains.Contains(emailHost))
        {
            reason = "email host is a generic email provider";
            return null;
        }

        if (IsExcludedHost(emailHost))
        {
            reason = "email host is excluded";
            return null;
        }

        var emailCore = GetDomainCore(emailHost);
        var websiteCore = GetDomainCore(websiteUrl.Host);
        var emailDomainScore = ScoreEmailDomain(emailCore, websiteCore, companyName);
        if (emailDomainScore <= 0)
        {
            reason = $"email domain core '{emailCore}' is not close to website core '{websiteCore}' or company name";
            return null;
        }

        var scoreParts = new List<string>
        {
            $"domain score {emailDomainScore} (email core '{emailCore}', website core '{websiteCore}')",
            $"page score contribution {Math.Min(siteEvidence.Score, 45)}"
        };

        var score = emailDomainScore + Math.Min(siteEvidence.Score, 45);
        if (hostSimilarityScore >= 55)
        {
            score += 20;
            scoreParts.Add($"host similarity bonus 20 (host score {hostSimilarityScore})");
        }
        else
        {
            scoreParts.Add($"no host similarity bonus (host score {hostSimilarityScore})");
        }

        if (IsRoleEmail(email))
        {
            score += 18;
            scoreParts.Add("role email bonus 18");
        }

        if (decodedHtml.Contains($"mailto:{email}", StringComparison.OrdinalIgnoreCase))
        {
            score += 12;
            scoreParts.Add("mailto bonus 12");
        }

        var localPart = email[..atIndex];
        if (localPart is "noreply" or "no-reply" or "donotreply" or "do-not-reply")
        {
            score -= 45;
            scoreParts.Add("no-reply penalty -45");
        }

        if (emailDomainScore < 55 && !siteEvidence.IsReputable && hostSimilarityScore < 55)
        {
            reason = $"weak domain/page evidence: {string.Join(", ", scoreParts)}";
            return null;
        }

        reason = string.Join(", ", scoreParts);
        return new ScoredEmail(email, score);
    }

    private static int ScoreHostSimilarityToCompany(string host, string companyName)
    {
        var domainCore = GetDomainCore(host);
        if (string.IsNullOrWhiteSpace(domainCore))
        {
            return 0;
        }

        return ScoreDomainAgainstCompany(domainCore, GetCompanyTokens(companyName));
    }

    private static int ScoreEmailDomain(string emailCore, string websiteCore, string companyName)
    {
        if (string.IsNullOrWhiteSpace(emailCore))
        {
            return 0;
        }

        var score = 0;
        if (!string.IsNullOrWhiteSpace(websiteCore))
        {
            if (emailCore.Equals(websiteCore, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 95);
            }
            else if (emailCore.Contains(websiteCore, StringComparison.OrdinalIgnoreCase)
                || websiteCore.Contains(emailCore, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 80);
            }
        }

        score = Math.Max(score, ScoreDomainAgainstCompany(emailCore, GetCompanyTokens(companyName)));
        return score;
    }

    private static int ScoreDomainAgainstCompany(string domainCore, IReadOnlyList<string> companyTokens)
    {
        if (companyTokens.Count == 0)
        {
            return 0;
        }

        var compactName = string.Concat(companyTokens);
        if (compactName.Length >= 5
            && domainCore.Equals(compactName, StringComparison.OrdinalIgnoreCase))
        {
            return 95;
        }

        if (compactName.Length >= 5
            && (domainCore.Contains(compactName, StringComparison.OrdinalIgnoreCase)
                || compactName.Contains(domainCore, StringComparison.OrdinalIgnoreCase)))
        {
            return 85;
        }

        var matchedTokens = companyTokens
            .Count(token => domainCore.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (matchedTokens == 0)
        {
            return 0;
        }

        if (matchedTokens >= GetRequiredCompanyTokenMatches(companyTokens.Count))
        {
            return 70;
        }

        return companyTokens.Any(token =>
            token.Length >= 4 && domainCore.Contains(token, StringComparison.OrdinalIgnoreCase)
            || token.Length == 3 && domainCore.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            ? 45
            : 0;
    }

    private static CompanyPageEvidence ScoreCompanyPage(string html, string companyName)
    {
        var companyTokens = GetCompanyTokens(companyName);
        if (companyTokens.Count == 0)
        {
            return CompanyPageEvidence.Empty;
        }

        var decodedHtml = WebUtility.HtmlDecode(html);
        var importantText = $"{ExtractTitleText(decodedHtml)} {ExtractMetaText(decodedHtml)} {ExtractHeaderText(decodedHtml)}";
        var visibleText = StripHtml(decodedHtml);
        var compactName = string.Concat(companyTokens);
        var compactImportantText = CompactText(importantText);
        var compactVisibleText = CompactText(visibleText);
        var compactImportantOccurrences = compactName.Length >= 5
            ? CountTextOccurrences(compactImportantText, compactName)
            : 0;
        var compactVisibleOccurrences = compactName.Length >= 5
            ? CountTextOccurrences(compactVisibleText, compactName)
            : 0;
        var hasCompactImportantMatch = compactImportantOccurrences > 0;
        var hasCompactVisibleMatch = compactVisibleOccurrences > 0;

        var importantTokenCounts = CountCompanyTokenOccurrences(importantText, companyTokens);
        var visibleTokenCounts = CountCompanyTokenOccurrences(visibleText, companyTokens);
        var requiredMatches = GetRequiredCompanyTokenMatches(companyTokens.Count);
        var importantMatchedTokens = importantTokenCounts.Count(item => item.Value > 0);
        var visibleMatchedTokens = visibleTokenCounts.Count(item => item.Value > 0);
        var visibleOccurrences = visibleTokenCounts.Sum(item => item.Value);

        var score = 0;
        if (hasCompactImportantMatch)
        {
            score += 45;
        }
        else if (hasCompactVisibleMatch)
        {
            score += 30;
        }

        if (importantMatchedTokens >= requiredMatches)
        {
            score += 35;
        }
        else
        {
            score += importantMatchedTokens * 12;
        }

        if (visibleMatchedTokens >= requiredMatches)
        {
            score += 20;
        }

        if (hasCompactVisibleMatch)
        {
            score += Math.Min(compactVisibleOccurrences * 6, 24);
        }

        score += Math.Min(visibleOccurrences * 4, 32);

        var enoughRepeatedMentions = visibleMatchedTokens >= requiredMatches
            && visibleOccurrences >= Math.Max(3, requiredMatches * 2);
        var enoughCompactMentions = compactVisibleOccurrences >= 2;
        var hasStrongImportantMention = hasCompactImportantMatch
            || importantMatchedTokens >= requiredMatches;
        var isReputable = score >= ReputablePageScoreThreshold
            && (enoughCompactMentions || enoughRepeatedMentions || hasStrongImportantMention);

        var summary = $"required token matches={requiredMatches}, important matches={importantMatchedTokens}, visible matches={visibleMatchedTokens}, visible occurrences={visibleOccurrences}, compact important occurrences={compactImportantOccurrences}, compact visible occurrences={compactVisibleOccurrences}";
        return new CompanyPageEvidence(score, isReputable, summary);
    }

    private static Dictionary<string, int> CountCompanyTokenOccurrences(string text, IReadOnlyList<string> companyTokens)
    {
        var counts = companyTokens.ToDictionary(token => token, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenRegex.Matches(text.ToLowerInvariant()))
        {
            var token = match.Value;
            if (counts.ContainsKey(token))
            {
                counts[token]++;
            }
        }

        return counts;
    }

    private static int GetRequiredCompanyTokenMatches(int tokenCount)
    {
        return tokenCount <= 2
            ? tokenCount
            : (int)Math.Ceiling(tokenCount * 2 / 3d);
    }

    private static string ExtractTitleText(string html)
    {
        var match = Regex.Match(
            html,
            @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        return match.Success ? StripHtml(match.Groups[1].Value) : string.Empty;
    }

    private static string ExtractMetaText(string html)
    {
        return string.Join(
            ' ',
            Regex.Matches(
                    html,
                    @"<meta\b[^>]*(?:name|property)=[""'](?:description|og:title|og:site_name|twitter:title)[""'][^>]*\bcontent=[""']([^""']*)[""'][^>]*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)
                .Select(match => match.Groups[1].Value));
    }

    private static string ExtractHeaderText(string html)
    {
        return string.Join(
            ' ',
            Regex.Matches(
                    html,
                    @"<h[1-3]\b[^>]*>(.*?)</h[1-3]>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)
                .Select(match => StripHtml(match.Groups[1].Value)));
    }

    private static string StripHtml(string html)
    {
        var withoutScripts = Regex.Replace(
            html,
            @"<(script|style)\b[^>]*>.*?</\1>",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var withoutTags = Regex.Replace(
            withoutScripts,
            "<[^>]+>",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        return Regex.Replace(withoutTags, @"\s+", " ", RegexOptions.CultureInvariant);
    }

    private static string CompactText(string text)
    {
        return string.Concat(TokenRegex
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value));
    }

    private static int CountTextOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count >= MaxDiagnosticsPerCompany)
        {
            return;
        }

        diagnostics.Add(message);
    }

    private static void AddDiagnosticMessages(
        List<string> messages,
        CompanyContactCandidate candidate,
        IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        AddMessage(messages, $"Diagnostics for {candidate.CompanyName}:");
        foreach (var diagnostic in diagnostics)
        {
            AddMessage(messages, $"  - {diagnostic}");
        }
    }

    private static string DescribeUris(IReadOnlyList<Uri> uris, int maxItems)
    {
        if (uris.Count == 0)
        {
            return "none";
        }

        var shownUris = uris
            .Take(maxItems)
            .Select(uri => uri.ToString());
        var suffix = uris.Count > maxItems
            ? $" ... (+{uris.Count - maxItems} more)"
            : string.Empty;

        return $"{string.Join(", ", shownUris)}{suffix}";
    }

    private sealed record CompanyContactCandidate(
        Guid Id,
        string CompanyNumber,
        string CompanyName);

    private sealed record CompanyContactMatch(
        string Email,
        Uri WebsiteUrl,
        Uri SourceUrl,
        int Score);

    private sealed record ScoredEmail(
        string Email,
        int Score);

    private sealed record CompanyPageEvidence(
        int Score,
        bool IsReputable,
        string Summary)
    {
        public static CompanyPageEvidence Empty { get; } = new(0, false, "no company evidence yet");

        public CompanyPageEvidence BetterOf(CompanyPageEvidence other)
        {
            return other.Score > Score ? other : this;
        }
    }

    private sealed class ContactAnchor
    {
        public string? Href { get; set; }

        public string? Text { get; set; }
    }
}
