using System.Globalization;
using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using PortalScraper.Data;
using PortalScraper.Services.Documents;

namespace PortalScraper.Services;

public interface IPlanningPortalScraper
{
    Task<WeeklyScrapeResult> ScrapeWeeklyAsync(CancellationToken cancellationToken = default);
}

public sealed record WeeklyScrapeResult(
    int AuthoritiesProcessed,
    int ApplicationsScraped,
    int ApplicationsSaved,
    int DocumentsScraped,
    int DocumentsSaved,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    IReadOnlyList<string> Messages);

public sealed class PlanningPortalScraper(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPlanningDocumentContentService documentContentService,
    ILogger<PlanningPortalScraper> logger) : IPlanningPortalScraper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public async Task<WeeklyScrapeResult> ScrapeWeeklyAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var scrapeTimer = Stopwatch.StartNew();
        var messages = new List<string>();
        var applicationsScraped = 0;
        var applicationsSaved = 0;
        var documentsScraped = 0;
        var documentsSaved = 0;
        var authoritiesProcessed = 0;

        logger.LogInformation("Starting weekly planning import scrape at {StartedAtUtc}", startedAt);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var authorities = await db.PlanningAuthorities
            .AsNoTracking()
            .Where(authority => authority.Website != null && authority.Website != "")
            .OrderBy(authority => authority.Name)
            .ToListAsync(cancellationToken);

        logger.LogInformation("Found {AuthorityCount} planning authorities with website URLs to scrape", authorities.Count);

        if (authorities.Count == 0)
        {
            logger.LogInformation("Weekly planning import scrape finished without work because no authorities have website URLs");
            messages.Add("No planning authorities with website URLs were found.");

            return new WeeklyScrapeResult(
                authoritiesProcessed,
                applicationsScraped,
                applicationsSaved,
                documentsScraped,
                documentsSaved,
                startedAt,
                DateTime.UtcNow,
                messages);
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        logger.LogDebug("Launched headless Chromium for weekly planning import scrape");

        foreach (var authority in authorities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorityTimer = Stopwatch.StartNew();

            try
            {
                logger.LogInformation(
                    "Starting weekly planning scrape for {PlanningAuthorityName} ({PlanningAuthorityId}) at {PlanningAuthorityWebsite}",
                    authority.Name,
                    authority.Id,
                    authority.Website);

                var authorityScrapeResult = await ScrapeAuthorityAsync(browser, authority, messages, cancellationToken);
                authoritiesProcessed++;
                var scrapedApplications = authorityScrapeResult.Applications;
                applicationsScraped += scrapedApplications.Count;
                var authorityDocumentsScraped = scrapedApplications.Sum(application => application.Documents.Count);
                documentsScraped += authorityDocumentsScraped;

                if (authorityScrapeResult.Skipped)
                {
                    logger.LogInformation(
                        "Skipped {PlanningAuthorityName} because documents already exist for the selected weekly list",
                        authority.Name);
                    continue;
                }

                logger.LogInformation(
                    "Scraped {ApplicationCount} applications and {DocumentCount} documents for {PlanningAuthorityName}; importing to database",
                    scrapedApplications.Count,
                    authorityDocumentsScraped,
                    authority.Name);

                var saveResult = await SaveApplicationsAsync(authority.Id, scrapedApplications, cancellationToken);
                applicationsSaved += saveResult.ApplicationsSaved;
                documentsSaved += saveResult.DocumentsSaved;

                logger.LogInformation(
                    "Finished {PlanningAuthorityName}: imported {ApplicationsSaved} applications and {DocumentsSaved} new documents in {ElapsedMilliseconds} ms",
                    authority.Name,
                    saveResult.ApplicationsSaved,
                    saveResult.DocumentsSaved,
                    authorityTimer.ElapsedMilliseconds);

                messages.Add(
                    $"{authority.Name}: scraped {scrapedApplications.Count} applications and {authorityDocumentsScraped} documents.");
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to scrape planning authority {PlanningAuthorityName} ({PlanningAuthorityId}) after {ElapsedMilliseconds} ms",
                    authority.Name,
                    authority.Id,
                    authorityTimer.ElapsedMilliseconds);
                messages.Add($"{authority.Name}: scrape failed - {ex.Message}");
            }
        }

        scrapeTimer.Stop();
        logger.LogInformation(
            "Finished weekly planning import scrape in {ElapsedMilliseconds} ms. Authorities={AuthoritiesProcessed}, ApplicationsScraped={ApplicationsScraped}, ApplicationsSaved={ApplicationsSaved}, DocumentsScraped={DocumentsScraped}, DocumentsSaved={DocumentsSaved}",
            scrapeTimer.ElapsedMilliseconds,
            authoritiesProcessed,
            applicationsScraped,
            applicationsSaved,
            documentsScraped,
            documentsSaved);

        return new WeeklyScrapeResult(
            authoritiesProcessed,
            applicationsScraped,
            applicationsSaved,
            documentsScraped,
            documentsSaved,
            startedAt,
            DateTime.UtcNow,
            messages);
    }

    private async Task<AuthorityScrapeResult> ScrapeAuthorityAsync(
        IBrowser browser,
        PlanningAuthority authority,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(authority.Website!);
        var applications = new List<ScrapedApplication>();

        logger.LogDebug("Opening browser context for {PlanningAuthorityName} using base URI {BaseUri}", authority.Name, baseUri);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        page.SetDefaultNavigationTimeout(60_000);

        logger.LogDebug("Navigating to planning authority homepage {BaseUri}", baseUri);
        await page.GotoAsync(baseUri.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });

        logger.LogDebug("Opening weekly list for {PlanningAuthorityName}", authority.Name);
        await OpenWeeklyListAsync(page, baseUri);

        if (await ShouldSkipLoadedWeekAsync(page, authority, messages, cancellationToken))
        {
            return new AuthorityScrapeResult(applications, Skipped: true);
        }

        logger.LogDebug("Submitting weekly list search for {PlanningAuthorityName}", authority.Name);
        await SubmitWeeklySearchAsync(page);
        await TrySetResultsPerPageToMaximumAsync(page);

        var resultLinks = await CollectResultLinksAsync(page, baseUri, cancellationToken);
        logger.LogInformation(
            "Collected {ResultCount} weekly planning result links for {PlanningAuthorityName}",
            resultLinks.Count,
            authority.Name);

        if (resultLinks.Count == 0 && await TrySelectPreviousWeekAsync(page, baseUri))
        {
            logger.LogInformation(
                "{PlanningAuthorityName}: default weekly list returned no applications; retrying the previous listed week",
                authority.Name);
            messages.Add($"{authority.Name}: default weekly list returned no applications, retried the previous listed week.");

            if (await ShouldSkipLoadedWeekAsync(page, authority, messages, cancellationToken))
            {
                return new AuthorityScrapeResult(applications, Skipped: true);
            }

            await SubmitWeeklySearchAsync(page);
            await TrySetResultsPerPageToMaximumAsync(page);

            resultLinks = await CollectResultLinksAsync(page, baseUri, cancellationToken);
            logger.LogInformation(
                "Collected {ResultCount} previous-week planning result links for {PlanningAuthorityName}",
                resultLinks.Count,
                authority.Name);
        }

        for (var index = 0; index < resultLinks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultLink = resultLinks[index];

            try
            {
                logger.LogInformation(
                    "Scraping planning application {ApplicationIndex}/{ApplicationCount} for {PlanningAuthorityName}: {ApplicationUrl}",
                    index + 1,
                    resultLinks.Count,
                    authority.Name,
                    resultLink.Url);

                applications.Add(await ScrapeApplicationAsync(page, baseUri, resultLink, cancellationToken));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "{PlanningAuthorityName}: failed to scrape planning application {ApplicationUrl}",
                    authority.Name,
                    resultLink.Url);
                messages.Add($"{authority.Name}: failed to scrape {resultLink.Url} - {ex.Message}");
            }
        }

        return new AuthorityScrapeResult(applications, Skipped: false);
    }

    private async Task<bool> ShouldSkipLoadedWeekAsync(
        IPage page,
        PlanningAuthority authority,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var week = await TryGetSelectedWeekAsync(page);
        if (week is null)
        {
            logger.LogDebug(
                "{PlanningAuthorityName}: could not identify the selected weekly-list date; scraping will continue",
                authority.Name);
            return false;
        }

        if (!await HasPlanningDataForWeekAsync(authority.Id, week.WeekStart, cancellationToken))
        {
            return false;
        }

        logger.LogInformation(
            "{PlanningAuthorityName}: skipping week beginning {WeekStart:yyyy-MM-dd}; planning data is already loaded",
            authority.Name,
            week.WeekStart);
        messages.Add($"{authority.Name}: skipped week beginning {week.WeekStart:dd MMM yyyy}; planning data already loaded.");

        return true;
    }

    private async Task<bool> HasPlanningDataForWeekAsync(
        Guid authorityId,
        DateTime weekStart,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var weekEnd = weekStart.AddDays(7);

        var hasDocumentsForWeek = await db.PlanningDocuments
            .AnyAsync(
                document => document.PublishedDate >= weekStart
                    && document.PublishedDate < weekEnd
                    && document.PlanningApplication.PlanningAuthorityId == authorityId,
                cancellationToken);

        if (hasDocumentsForWeek)
        {
            return true;
        }

        return await db.PlanningApplications
            .AnyAsync(
                application => application.PlanningAuthorityId == authorityId
                    && ((application.ValidatedDate >= weekStart && application.ValidatedDate < weekEnd)
                        || (application.ReceivedDate >= weekStart && application.ReceivedDate < weekEnd)),
                cancellationToken);
    }

    private static async Task OpenWeeklyListAsync(IPage page, Uri baseUri)
    {
        var weeklyLink = page
            .Locator("a[href*='action=weeklyList'][href*='searchType=Application'], #planListSearch a")
            .First;

        if (await weeklyLink.CountAsync() > 0)
        {
            var href = await weeklyLink.GetAttributeAsync("href");
            if (!string.IsNullOrWhiteSpace(href))
            {
                var weeklyUri = new Uri(baseUri, WebUtility.HtmlDecode(href));
                await page.GotoAsync(weeklyUri.ToString(), new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });

                return;
            }
        }

        var weeklyListUri = new Uri(baseUri, "search.do?action=weeklyList&searchType=Application");
        await page.GotoAsync(weeklyListUri.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
    }

    private static async Task SubmitWeeklySearchAsync(IPage page)
    {
        await page.Locator("#weeklyListForm input[type='submit'], input[type='submit'][value='Search']")
            .First
            .ClickAsync();

        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    }

    private static async Task<bool> TrySelectPreviousWeekAsync(IPage page, Uri baseUri)
    {
        var weeklyListUri = new Uri(baseUri, "search.do?action=weeklyList&searchType=Application");
        await page.GotoAsync(weeklyListUri.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });

        var weekSelect = page.Locator("#week");
        if (await weekSelect.CountAsync() == 0)
        {
            return false;
        }

        var weekValuesJson = await page.Locator("#week option").EvaluateAllAsync<string>(
            """
            options => JSON.stringify(options.map(option => option.value).filter(Boolean))
            """);
        var weekValues = JsonSerializer.Deserialize<List<string>>(weekValuesJson, JsonOptions) ?? [];

        if (weekValues.Count < 2)
        {
            return false;
        }

        var selectedWeek = await weekSelect.InputValueAsync();
        var selectedIndex = weekValues.FindIndex(value => value.Equals(selectedWeek, StringComparison.OrdinalIgnoreCase));
        var previousWeekIndex = selectedIndex >= 0 ? selectedIndex + 1 : 1;

        if (previousWeekIndex >= weekValues.Count)
        {
            return false;
        }

        await weekSelect.SelectOptionAsync(weekValues[previousWeekIndex]);

        return true;
    }

    private static async Task<WeeklyListSelection?> TryGetSelectedWeekAsync(IPage page)
    {
        var weekSelect = page.Locator("#week");
        if (await weekSelect.CountAsync() == 0)
        {
            return null;
        }

        var json = await weekSelect.EvaluateAsync<string>(
            """
            select => {
                const selectedOption = select.selectedOptions && select.selectedOptions.length > 0
                    ? select.selectedOptions[0]
                    : null;

                return JSON.stringify({
                    value: select.value || '',
                    label: selectedOption?.textContent || ''
                });
            }
            """);
        var selectedWeek = JsonSerializer.Deserialize<SelectedWeekDto>(json, JsonOptions);
        var weekStart = TryParseWeekStart(selectedWeek?.Value, selectedWeek?.Label);

        return weekStart is null
            ? null
            : new WeeklyListSelection(selectedWeek?.Value ?? string.Empty, selectedWeek?.Label ?? string.Empty, weekStart.Value);
    }

    private static DateTime? TryParseWeekStart(params string?[] values)
    {
        var formats = new[]
        {
            "d MMM yyyy",
            "dd MMM yyyy",
            "d MMMM yyyy",
            "dd MMMM yyyy",
            "ddd d MMM yyyy",
            "ddd dd MMM yyyy",
            "yyyy-MM-dd",
            "d/M/yyyy",
            "dd/MM/yyyy"
        };

        foreach (var value in values.Select(NormalizeText))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.GetCultureInfo("en-GB"),
                DateTimeStyles.None,
                out var exactDate))
            {
                return exactDate.Date;
            }

            var dateMatch = Regex.Match(value, @"\b\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4}\b");
            if (dateMatch.Success
                && DateTime.TryParseExact(
                    dateMatch.Value,
                    formats,
                    CultureInfo.GetCultureInfo("en-GB"),
                    DateTimeStyles.None,
                    out var matchedDate))
            {
                return matchedDate.Date;
            }
        }

        return null;
    }

    private static async Task TrySetResultsPerPageToMaximumAsync(IPage page)
    {
        var select = page.Locator("#resultsPerPage");

        if (await select.CountAsync() == 0)
        {
            return;
        }

        try
        {
            await select.SelectOptionAsync("100");

            var submit = page.Locator("#searchResults input[type='submit'], #searchfilters input[type='submit']").First;
            if (await submit.CountAsync() > 0)
            {
                await submit.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }
        }
        catch (PlaywrightException)
        {
            // Some councils expose fewer page-size options. Pagination still handles the remaining records.
        }
    }

    private static async Task<IReadOnlyList<SearchResultDto>> CollectResultLinksAsync(
        IPage page,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResultDto>();
        var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            visitedPages.Add(page.Url);
            results.AddRange(await ExtractSearchResultsAsync(page));

            var next = page.Locator("p.pager.bottom a.next, p.pager.top a.next").First;
            if (await next.CountAsync() == 0)
            {
                break;
            }

            var nextHref = await next.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(nextHref))
            {
                break;
            }

            var nextUrl = new Uri(baseUri, WebUtility.HtmlDecode(nextHref)).ToString();
            if (!visitedPages.Add(nextUrl))
            {
                break;
            }

            await page.GotoAsync(nextUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
        }

        return results
            .Where(result => !string.IsNullOrWhiteSpace(result.Url))
            .GroupBy(result => result.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static async Task<IReadOnlyList<SearchResultDto>> ExtractSearchResultsAsync(IPage page)
    {
        var json = await page.Locator("ul#searchresults > li.searchresult").EvaluateAllAsync<string>(
            """
            items => JSON.stringify(items.map(item => {
                const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                const link = item.querySelector('a[href*="applicationDetails.do"]');

                return {
                    title: clean(link?.textContent),
                    url: link?.href || link?.getAttribute('href') || '',
                    address: clean(item.querySelector('p.address')?.textContent),
                    meta: clean(item.querySelector('p.metaInfo')?.textContent)
                };
            }))
            """);

        return JsonSerializer.Deserialize<List<SearchResultDto>>(json, JsonOptions) ?? [];
    }

    private async Task<ScrapedApplication> ScrapeApplicationAsync(
        IPage page,
        Uri baseUri,
        SearchResultDto resultLink,
        CancellationToken cancellationToken)
    {
        var applicationUrl = new Uri(baseUri, WebUtility.HtmlDecode(resultLink.Url)).ToString();
        var sourceKey = ExtractQueryValue(applicationUrl, "keyVal");

        logger.LogDebug("Opening summary tab for planning application {ApplicationUrl}", applicationUrl);
        await page.GotoAsync(BuildApplicationTabUrl(applicationUrl, "summary"), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });

        var details = await ExtractDetailsAsync(page);
        var reference = GetValue(details, "Reference");
        var proposal = GetValue(details, "Proposal");
        var address = GetValue(details, "Address");

        var application = new ScrapedApplication
        {
            Title = FirstNonEmpty(proposal, resultLink.Title, reference) ?? "Untitled planning application",
            Description = FirstNonEmpty(proposal, resultLink.Title),
            Address = FirstNonEmpty(address, resultLink.Address),
            ApplicationReference = FirstNonEmpty(reference, ExtractReferenceFromMeta(resultLink.Meta)),
            SourceKey = sourceKey,
            SourceUrl = BuildApplicationTabUrl(applicationUrl, "summary"),
            Status = GetValue(details, "Status"),
            ReceivedDate = ParsePortalDate(GetValue(details, "Application Received")),
            ValidatedDate = ParsePortalDate(GetValue(details, "Application Validated"))
        };

        logger.LogInformation(
            "Scraping planning application {ApplicationReference} ({SourceKey}) from {ApplicationUrl}",
            application.ApplicationReference ?? "unknown reference",
            application.SourceKey ?? "unknown key",
            applicationUrl);

        logger.LogDebug("Opening contacts tab for planning application {ApplicationReference}", application.ApplicationReference ?? application.SourceKey);
        await page.GotoAsync(BuildApplicationTabUrl(applicationUrl, "contacts"), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        ApplyContacts(application, await ExtractContactsAsync(page));

        logger.LogDebug("Opening details tab for planning application {ApplicationReference}", application.ApplicationReference ?? application.SourceKey);
        await page.GotoAsync(BuildApplicationTabUrl(applicationUrl, "details"), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        ApplyFurtherInformation(application, await ExtractFurtherInformationAsync(page));

        logger.LogDebug("Opening documents tab for planning application {ApplicationReference}", application.ApplicationReference ?? application.SourceKey);
        await page.GotoAsync(BuildApplicationTabUrl(applicationUrl, "documents"), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        var documents = await ExtractDocumentsAsync(page);
        logger.LogInformation(
            "Found {DocumentCount} documents for planning application {ApplicationReference}",
            documents.Count,
            application.ApplicationReference ?? application.SourceKey ?? applicationUrl);

        for (var index = 0; index < documents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents[index];

            logger.LogInformation(
                "Extracting document {DocumentIndex}/{DocumentCount} for planning application {ApplicationReference}: {DocumentName} ({DocumentType})",
                index + 1,
                documents.Count,
                application.ApplicationReference ?? application.SourceKey ?? applicationUrl,
                document.Name,
                document.DocumentType);

            var extraction = await documentContentService.ExtractAsync(document.Url, page.Context, cancellationToken);
            document.ContentText = extraction.ContentText;
            document.FileName = extraction.FileName;
            document.ContentType = extraction.ContentType;
            document.ParseStatus = extraction.ParseStatus;
            document.ParseError = extraction.ParseError;
            document.ParsedAt = DateTime.UtcNow;

            logger.LogInformation(
                "Document extraction finished with status {ParseStatus} for {DocumentFileName}; extracted {CharacterCount} characters",
                document.ParseStatus,
                document.FileName ?? document.Name,
                document.ContentText?.Length ?? 0);

            application.Documents.Add(document);
        }

        return application;
    }

    private static async Task<Dictionary<string, string>> ExtractDetailsAsync(IPage page)
    {
        var json = await page.Locator("#simpleDetailsTable tr").EvaluateAllAsync<string>(
            """
            rows => JSON.stringify(Object.fromEntries(rows.map(row => {
                const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                return [
                    clean(row.querySelector('th')?.textContent),
                    clean(row.querySelector('td')?.textContent)
                ];
            }).filter(([key, value]) => key && value)))
            """);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? [];
    }

    private static async Task<IReadOnlyList<ContactSectionDto>> ExtractContactsAsync(IPage page)
    {
        var json = await page.Locator(".tabcontainer > div").EvaluateAllAsync<string>(
            """
            sections => {
                const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                const decodeEmail = encoded => {
                    if (!encoded || encoded.length < 4) {
                        return '';
                    }

                    const key = parseInt(encoded.substr(0, 2), 16);
                    let output = '';

                    for (let i = 2; i < encoded.length; i += 2) {
                        output += String.fromCharCode(parseInt(encoded.substr(i, 2), 16) ^ key);
                    }

                    return output;
                };

                return JSON.stringify(sections.map(section => {
                    const heading = clean(section.querySelector('h3, h2')?.textContent);
                    const name = clean(section.querySelector('p')?.textContent);
                    const rows = {};

                    section.querySelectorAll('tr').forEach(row => {
                        const label = clean(row.querySelector('th')?.textContent).toLowerCase();
                        const cell = row.querySelector('td');
                        const protectedEmail = cell?.querySelector('[data-cfemail]')?.getAttribute('data-cfemail');

                        if (label) {
                            rows[label] = protectedEmail ? decodeEmail(protectedEmail) : clean(cell?.textContent);
                        }
                    });

                    return { heading, name, rows };
                }).filter(section => section.heading));
            }
            """);

        return JsonSerializer.Deserialize<List<ContactSectionDto>>(json, JsonOptions) ?? [];
    }

    private static async Task<FurtherInformationDto> ExtractFurtherInformationAsync(IPage page)
    {
        var json = await page.Locator("#applicationDetails tr").EvaluateAllAsync<string>(
            """
            rows => JSON.stringify(Object.fromEntries(rows.map(row => {
                const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                return [
                    clean(row.querySelector('th')?.textContent),
                    clean(row.querySelector('td')?.textContent)
                ];
            }).filter(([key, value]) => key && value)))
            """);

        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? [];

        return new FurtherInformationDto(fields);
    }

    private static async Task<IReadOnlyList<ScrapedDocument>> ExtractDocumentsAsync(IPage page)
    {
        if (await page.Locator("#Documents tr").CountAsync() == 0)
        {
            return [];
        }

        var json = await page.Locator("#Documents tr").EvaluateAllAsync<string>(
            """
            rows => JSON.stringify(rows.slice(1).map(row => {
                const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                const cells = Array.from(row.querySelectorAll('td'));
                const viewLink = row.querySelector('a[title*="View"], a.recaptcha-link[href]');

                if (cells.length < 6 || !viewLink) {
                    return null;
                }

                return {
                    publishedDate: clean(cells[1]?.textContent),
                    documentType: clean(cells[2]?.textContent),
                    description: clean(cells[4]?.textContent),
                    url: viewLink.href || viewLink.getAttribute('href') || ''
                };
            }).filter(Boolean))
            """);

        var documents = JsonSerializer.Deserialize<List<DocumentDto>>(json, JsonOptions) ?? [];

        return documents
            .Where(document => !string.IsNullOrWhiteSpace(document.Url))
            .Select(document => new ScrapedDocument
            {
                Name = FirstNonEmpty(document.Description, document.DocumentType, "Document")!,
                DocumentType = FirstNonEmpty(document.DocumentType, "Document")!,
                Url = document.Url,
                PublishedDate = ParsePortalDate(document.PublishedDate)
            })
            .ToList();
    }

    private static void ApplyContacts(ScrapedApplication application, IReadOnlyList<ContactSectionDto> contacts)
    {
        foreach (var contact in contacts)
        {
            if (contact.Heading.Contains("Applicant", StringComparison.OrdinalIgnoreCase))
            {
                application.ApplicantName = FirstNonEmpty(contact.Name, application.ApplicantName);
                application.ApplicantEmail = FirstNonEmpty(GetContactValue(contact, "email"), application.ApplicantEmail);
                application.ApplicantPhone = FirstNonEmpty(GetContactValue(contact, "phone"), application.ApplicantPhone);
            }
            else if (contact.Heading.Contains("Agent", StringComparison.OrdinalIgnoreCase))
            {
                application.AgentName = FirstNonEmpty(contact.Name, application.AgentName);
                application.AgentEmail = FirstNonEmpty(GetContactValue(contact, "email"), application.AgentEmail);
                application.AgentPhone = FirstNonEmpty(GetContactValue(contact, "phone"), application.AgentPhone);
            }
        }
    }

    private static void ApplyFurtherInformation(ScrapedApplication application, FurtherInformationDto furtherInformation)
    {
        application.CompanyName = FirstNonEmpty(furtherInformation.CompanyName, application.CompanyName);
    }

    private async Task<SaveResult> SaveApplicationsAsync(
        Guid authorityId,
        IReadOnlyList<ScrapedApplication> scrapedApplications,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Importing {ApplicationCount} scraped planning applications for authority {PlanningAuthorityId}",
            scrapedApplications.Count,
            authorityId);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var applicationsSaved = 0;
        var documentsSaved = 0;

        foreach (var scrapedApplication in scrapedApplications)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(scrapedApplication.ApplicationReference)
                && string.IsNullOrWhiteSpace(scrapedApplication.SourceKey))
            {
                logger.LogWarning(
                    "Skipping scraped planning application with no application reference or source key. Title={ApplicationTitle}",
                    scrapedApplication.Title);
                continue;
            }

            var existingApplication = await FindExistingApplicationAsync(db, authorityId, scrapedApplication, cancellationToken);
            var isNewApplication = existingApplication is null;

            if (existingApplication is null)
            {
                existingApplication = new PlanningApplication
                {
                    Id = Guid.NewGuid(),
                    PlanningAuthorityId = authorityId
                };

                db.PlanningApplications.Add(existingApplication);
            }

            logger.LogDebug(
                "{ImportAction} planning application {ApplicationReference} ({SourceKey})",
                isNewApplication ? "Creating" : "Updating",
                scrapedApplication.ApplicationReference,
                scrapedApplication.SourceKey);

            ApplyApplication(existingApplication, scrapedApplication);
            applicationsSaved++;

            foreach (var scrapedDocument in scrapedApplication.Documents)
            {
                if (string.IsNullOrWhiteSpace(scrapedDocument.Url))
                {
                    logger.LogWarning(
                        "Skipping planning document with no URL for application {ApplicationReference} ({SourceKey})",
                        scrapedApplication.ApplicationReference,
                        scrapedApplication.SourceKey);
                    continue;
                }

                var existingDocument = existingApplication.PlanningDocuments
                    .FirstOrDefault(document => document.Url.Equals(scrapedDocument.Url, StringComparison.OrdinalIgnoreCase));

                if (existingDocument is null)
                {
                    existingDocument = new PlanningDocument
                    {
                        Id = Guid.NewGuid(),
                        PlanningApplicationId = existingApplication.Id
                    };

                    existingApplication.PlanningDocuments.Add(existingDocument);
                    documentsSaved++;
                    logger.LogDebug(
                        "Creating planning document record for application {ApplicationReference}: {DocumentUrl}",
                        scrapedApplication.ApplicationReference,
                        scrapedDocument.Url);
                }
                else
                {
                    logger.LogDebug(
                        "Updating planning document record for application {ApplicationReference}: {DocumentUrl}",
                        scrapedApplication.ApplicationReference,
                        scrapedDocument.Url);
                }

                existingDocument.Name = Truncate(scrapedDocument.Name, 255) ?? "Document";
                existingDocument.DocumentType = Truncate(scrapedDocument.DocumentType, 50) ?? "Document";
                existingDocument.Url = Truncate(scrapedDocument.Url, 500) ?? scrapedDocument.Url;
                existingDocument.PublishedDate = scrapedDocument.PublishedDate;
                existingDocument.ContentText = scrapedDocument.ContentText;
                existingDocument.FileName = Truncate(scrapedDocument.FileName, 255);
                existingDocument.ContentType = Truncate(scrapedDocument.ContentType, 255);
                existingDocument.ParseStatus = Truncate(scrapedDocument.ParseStatus, 50);
                existingDocument.ParseError = Truncate(scrapedDocument.ParseError, 1000);
                existingDocument.ParsedAt = scrapedDocument.ParsedAt;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Imported {ApplicationsSaved} planning applications and {DocumentsSaved} new planning documents for authority {PlanningAuthorityId}",
            applicationsSaved,
            documentsSaved,
            authorityId);

        return new SaveResult(applicationsSaved, documentsSaved);
    }

    private static async Task<PlanningApplication?> FindExistingApplicationAsync(
        ApplicationDbContext db,
        Guid authorityId,
        ScrapedApplication scrapedApplication,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(scrapedApplication.ApplicationReference))
        {
            return await db.PlanningApplications
                .Include(application => application.PlanningDocuments)
                .FirstOrDefaultAsync(
                    application => application.PlanningAuthorityId == authorityId
                        && application.ApplicationReference == scrapedApplication.ApplicationReference,
                    cancellationToken);
        }

        return await db.PlanningApplications
            .Include(application => application.PlanningDocuments)
            .FirstOrDefaultAsync(
                application => application.PlanningAuthorityId == authorityId
                    && application.SourceKey == scrapedApplication.SourceKey,
                cancellationToken);
    }

    private static void ApplyApplication(PlanningApplication target, ScrapedApplication source)
    {
        target.Title = source.Title;
        target.Description = source.Description;
        target.Address = Truncate(source.Address, 500);
        target.ApplicationReference = Truncate(source.ApplicationReference, 100);
        target.SourceKey = Truncate(source.SourceKey, 100);
        target.SourceUrl = Truncate(source.SourceUrl, 500);
        target.Status = Truncate(source.Status, 100);
        target.ReceivedDate = source.ReceivedDate;
        target.ValidatedDate = source.ValidatedDate;
        target.ApplicantName = Truncate(source.ApplicantName, 255);
        target.ApplicantEmail = Truncate(source.ApplicantEmail, 255);
        target.ApplicantPhone = Truncate(source.ApplicantPhone, 50);
        target.AgentName = Truncate(source.AgentName, 255);
        target.AgentEmail = Truncate(source.AgentEmail, 255);
        target.AgentPhone = Truncate(source.AgentPhone, 50);
        target.CompanyName = Truncate(source.CompanyName, 255);
        target.ScrapedAt = DateTime.UtcNow;
    }

    private static Uri NormalizeBaseUri(string website)
    {
        var trimmed = website.Trim();
        if (!trimmed.EndsWith("/", StringComparison.Ordinal))
        {
            trimmed += "/";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static string BuildApplicationTabUrl(string applicationUrl, string activeTab)
    {
        var key = ExtractQueryValue(applicationUrl, "keyVal");
        if (string.IsNullOrWhiteSpace(key))
        {
            return applicationUrl;
        }

        var builder = new UriBuilder(applicationUrl)
        {
            Query = $"activeTab={Uri.EscapeDataString(activeTab)}&keyVal={Uri.EscapeDataString(key)}"
        };

        return builder.Uri.ToString();
    }

    private static string? ExtractReferenceFromMeta(string? meta)
    {
        if (string.IsNullOrWhiteSpace(meta))
        {
            return null;
        }

        var match = Regex.Match(meta, @"Ref\.?\s*No:\s*(?<reference>.*?)(?:\s*\||$)", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeText(match.Groups["reference"].Value) : null;
    }

    private static string? ExtractQueryValue(string url, string key)
    {
        var match = Regex.Match(url, $@"[?&]{Regex.Escape(key)}=([^&]+)", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.UrlDecode(match.Groups[1].Value) : null;
    }

    private static string? GetValue(Dictionary<string, string> details, string key)
    {
        return details.TryGetValue(key, out var value) ? NormalizeText(value) : null;
    }

    private static string? GetContactValue(ContactSectionDto contact, string key)
    {
        if (contact.Rows.TryGetValue(key, out var value))
        {
            return NormalizeText(value);
        }

        var matchingKey = contact.Rows.Keys.FirstOrDefault(rowKey => rowKey.Contains(key, StringComparison.OrdinalIgnoreCase));
        return matchingKey is null ? null : NormalizeText(contact.Rows[matchingKey]);
    }

    private static DateTime? ParsePortalDate(string? value)
    {
        value = NormalizeText(value);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var formats = new[]
        {
            "ddd dd MMM yyyy",
            "dd MMM yyyy",
            "d MMM yyyy"
        };

        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate))
        {
            return exactDate;
        }

        return DateTime.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var parsedDate)
            ? parsedDate
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.Select(NormalizeText).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : WhitespaceRegex.Replace(value, " ").Trim();
    }

    private static string? Truncate(string? value, int maxLength)
    {
        value = NormalizeText(value);
        return value is null || value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record SaveResult(int ApplicationsSaved, int DocumentsSaved);

    private sealed record AuthorityScrapeResult(IReadOnlyList<ScrapedApplication> Applications, bool Skipped);

    private sealed record WeeklyListSelection(string Value, string Label, DateTime WeekStart);

    private sealed class SelectedWeekDto
    {
        public string Value { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;
    }

    private sealed class SearchResultDto
    {
        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string? Address { get; set; }

        public string? Meta { get; set; }
    }

    private sealed class ContactSectionDto
    {
        public string Heading { get; set; } = string.Empty;

        public string? Name { get; set; }

        public Dictionary<string, string> Rows { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FurtherInformationDto(Dictionary<string, string> fields)
    {
        public IReadOnlyDictionary<string, string> Fields { get; } = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);

        public string? ApplicationType => Get("Application Type");

        public string? Decision => Get("Decision");

        public string? ActualDecisionLevel => Get("Actual Decision Level");

        public string? ExpectedDecisionLevel => Get("Expected Decision Level");

        public string? CaseOfficer => Get("Case Officer");

        public string? Parish => Get("Parish");

        public string? Ward => Get("Ward");

        public string? DistrictReference => Get("District Reference");

        public string? ApplicantName => Get("Applicant Name");

        public string? ApplicantAddress => Get("Applicant Address");

        public string? AgentName => Get("Agent Name");

        public string? AgentCompanyName => Get("Agent Company Name");

        public string? AgentAddress => Get("Agent Address");

        public string? EnvironmentalAssessmentRequested => Get("Environmental Assessment Requested");

        public string? CompanyName => FirstNonEmpty(
            AgentCompanyName,
            Get("Applicant Company Name"),
            Get("Company Name"),
            Get("Agent Company"),
            Get("Applicant Company"),
            Get("Agent Organisation Name"),
            Get("Applicant Organisation Name"),
            Get("Organisation Name"),
            FindByLabel("company"),
            FindByLabel("organisation"),
            FindByLabel("organization"));

        private string? Get(string key)
        {
            return Fields.TryGetValue(key, out var value) ? NormalizeText(value) : null;
        }

        private string? FindByLabel(string label)
        {
            return Fields
                .Where(field => field.Key.Contains(label, StringComparison.OrdinalIgnoreCase)
                    && !field.Key.Contains("registration", StringComparison.OrdinalIgnoreCase)
                    && !field.Key.Contains("reference", StringComparison.OrdinalIgnoreCase)
                    && !field.Key.Contains("number", StringComparison.OrdinalIgnoreCase)
                    && !field.Key.Contains("email", StringComparison.OrdinalIgnoreCase)
                    && !field.Key.Contains("phone", StringComparison.OrdinalIgnoreCase)
                    && !field.Key.Contains("telephone", StringComparison.OrdinalIgnoreCase))
                .Select(field => NormalizeText(field.Value))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
    }

    private sealed class DocumentDto
    {
        public string? PublishedDate { get; set; }

        public string? DocumentType { get; set; }

        public string? Description { get; set; }

        public string Url { get; set; } = string.Empty;
    }

    private sealed class ScrapedApplication
    {
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? Address { get; set; }

        public string? ApplicationReference { get; set; }

        public string? SourceKey { get; set; }

        public string? SourceUrl { get; set; }

        public string? Status { get; set; }

        public DateTime? ReceivedDate { get; set; }

        public DateTime? ValidatedDate { get; set; }

        public string? ApplicantName { get; set; }

        public string? ApplicantEmail { get; set; }

        public string? ApplicantPhone { get; set; }

        public string? AgentName { get; set; }

        public string? AgentEmail { get; set; }

        public string? AgentPhone { get; set; }

        public string? CompanyName { get; set; }

        public List<ScrapedDocument> Documents { get; } = [];
    }

    private sealed class ScrapedDocument
    {
        public string Name { get; set; } = string.Empty;

        public string DocumentType { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public DateTime? PublishedDate { get; set; }

        public string? ContentText { get; set; }

        public string? FileName { get; set; }

        public string? ContentType { get; set; }

        public string? ParseStatus { get; set; }

        public string? ParseError { get; set; }

        public DateTime? ParsedAt { get; set; }
    }
}
