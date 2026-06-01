using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PortalScraper.Data;
using PortalScraper.Services.Documents;

namespace PortalScraper.Services;

public interface IPlanningPortalScraper
{
    Task<WeeklyScrapeResult> ScrapeWeeklyAsync(CancellationToken cancellationToken = default);

    Task<WeeklyScrapeResult> ScrapeThisWeekAsync(CancellationToken cancellationToken = default);
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
    IHostEnvironment hostEnvironment,
    IOptions<PlanningPortalScraperOptions> scraperOptions,
    ILogger<PlanningPortalScraper> logger) : IPlanningPortalScraper
{
    private const int TooManyRequestsRetryCount = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly TimeSpan DefaultTooManyRequestsRetryDelay = TimeSpan.FromMinutes(1);

    public Task<WeeklyScrapeResult> ScrapeWeeklyAsync(CancellationToken cancellationToken = default)
    {
        return ScrapeWeeklyAsync(scrapeMostRecentWeekOnly: false, cancellationToken);
    }

    public Task<WeeklyScrapeResult> ScrapeThisWeekAsync(CancellationToken cancellationToken = default)
    {
        return ScrapeWeeklyAsync(scrapeMostRecentWeekOnly: true, cancellationToken);
    }

    private async Task<WeeklyScrapeResult> ScrapeWeeklyAsync(
        bool scrapeMostRecentWeekOnly,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var scrapeTimer = Stopwatch.StartNew();
        var messages = new ConcurrentQueue<string>();
        var applicationsScraped = 0;
        var applicationsSaved = 0;
        var documentsScraped = 0;
        var documentsSaved = 0;
        var authoritiesProcessed = 0;
        var scraperSettings = scraperOptions.Value;
        var applicationImportConcurrency = NormalizeConcurrency(scraperSettings.MaxConcurrentApplicationImports);
        var documentDownloadConcurrency = NormalizeConcurrency(scraperSettings.MaxConcurrentDocumentDownloads);
        var scrapeCountersLock = new object();
        using var documentDownloadThrottle = new SemaphoreSlim(documentDownloadConcurrency);
        var fileLog = ScraperFileLogger.Create(
            hostEnvironment.ContentRootPath,
            scraperSettings.LogDirectory,
            startedAt,
            logger);

        var scrapeDescription = scrapeMostRecentWeekOnly
            ? "current week planning import scrape"
            : "weekly planning import scrape";

        logger.LogInformation("Starting {ScrapeDescription} at {StartedAtUtc}", scrapeDescription, startedAt);
        fileLog.WriteRunSummary($"Starting {scrapeDescription} at {FormatUtc(startedAt)}");
        if (fileLog.Enabled)
        {
            logger.LogInformation("Writing scraper text logs to {ScraperLogDirectory}", fileLog.RootDirectory);
            fileLog.WriteRunSummary($"Writing scraper text logs to {fileLog.RootDirectory}");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var authorities = await db.PlanningAuthorities
            .AsNoTracking()
            .Where(authority => authority.Website != null && authority.Website != "")
            .OrderBy(authority => authority.Name)
            .ToListAsync(cancellationToken);

        logger.LogInformation("Found {AuthorityCount} planning authorities with website URLs to scrape", authorities.Count);

        if (authorities.Count == 0)
        {
            logger.LogInformation("{ScrapeDescription} finished without work because no authorities have website URLs", scrapeDescription);
            fileLog.WriteRunSummary($"{scrapeDescription} finished without work because no authorities have website URLs.");
            messages.Enqueue("No planning authorities with website URLs were found.");

            return new WeeklyScrapeResult(
                authoritiesProcessed,
                applicationsScraped,
                applicationsSaved,
                documentsScraped,
                documentsSaved,
                startedAt,
                DateTime.UtcNow,
                messages.ToList());
        }

        logger.LogInformation(
            "Weekly planning import concurrency configured: applications={ApplicationImportConcurrency}, documents={DocumentDownloadConcurrency}",
            applicationImportConcurrency,
            documentDownloadConcurrency);
        fileLog.WriteRunSummary(
            $"Concurrency: application imports={applicationImportConcurrency}, document downloads={documentDownloadConcurrency}.");

        var browserFailureRetryCount = Math.Max(0, scraperSettings.BrowserFailureRetryCount);
        var browserRestartDelay = TimeSpan.FromMilliseconds(Math.Max(0, scraperSettings.BrowserRestartDelayMilliseconds));
        await using var browserSession = await StartBrowserSessionWithRetryAsync(
            fileLog,
            browserFailureRetryCount,
            browserRestartDelay,
            cancellationToken);

        var exhaustedAuthorityIds = new HashSet<Guid>();
        for (var weekNumber = 0; ; weekNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var authoritiesRemaining = authorities.Count - exhaustedAuthorityIds.Count;
            if (authoritiesRemaining == 0)
            {
                logger.LogInformation("Stopping weekly planning import scrape because every planning authority has run out of weekly dropdown options");
                fileLog.WriteRunSummary("Stopping weekly planning import scrape because every planning authority has run out of weekly dropdown options.");
                break;
            }

            logger.LogInformation(
                "Starting weekly planning scrape pass for dropdown weekNumber {WeekNumber}; {AuthorityCount} authorities still have possible weeks",
                weekNumber,
                authoritiesRemaining);
            fileLog.WriteRunSummary(
                $"Starting weekly planning scrape pass for dropdown weekNumber {weekNumber}; authorities still with possible weeks={authoritiesRemaining}.");

            var authoritiesWithSelectedWeek = 0;
            var queuedApplicationLinks = new List<QueuedApplicationLink>();
            foreach (var authority in authorities)
            {
                if (exhaustedAuthorityIds.Contains(authority.Id))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var authorityTimer = Stopwatch.StartNew();
                var authorityStartedAt = DateTime.UtcNow;
                var authorityLog = fileLog.CreateAuthorityLog(authority);
                authorityLog.StartRun(authority, authorityStartedAt);

                try
                {
                    logger.LogInformation(
                        "Collecting weekly planning links for {PlanningAuthorityName} ({PlanningAuthorityId}) at {PlanningAuthorityWebsite} using dropdown weekNumber {WeekNumber}",
                        authority.Name,
                        authority.Id,
                        authority.Website,
                        weekNumber);
                    authorityLog.WriteSummary($"Collecting weekly planning links for dropdown weekNumber {weekNumber}.");

                    var authorityLinkResult = await CollectAuthorityApplicationLinksWithBrowserRecoveryAsync(
                        browserSession,
                        authority,
                        authorityLog,
                        messages,
                        weekNumber,
                        browserFailureRetryCount,
                        browserRestartDelay,
                        cancellationToken);

                    if (authorityLinkResult.WeekUnavailable)
                    {
                        exhaustedAuthorityIds.Add(authority.Id);
                        logger.LogInformation(
                            "Skipped {PlanningAuthorityName} because the weekly dropdown does not contain weekNumber {WeekNumber}",
                            authority.Name,
                            weekNumber);
                        authorityLog.WriteSummary(
                            $"Skipped after {authorityTimer.ElapsedMilliseconds} ms because the weekly dropdown does not contain weekNumber {weekNumber}.");
                        continue;
                    }

                    authoritiesWithSelectedWeek++;
                    authoritiesProcessed++;
                    var selectedWeekDescription = DescribeWeeklySelection(authorityLinkResult.SelectedWeek, weekNumber);
                    var resultLinks = authorityLinkResult.ResultLinks;

                    if (authorityLinkResult.Skipped)
                    {
                        logger.LogInformation(
                            "Skipped {PlanningAuthorityName} because {SelectedWeek} returned no new applications to scrape",
                            authority.Name,
                            selectedWeekDescription);
                        authorityLog.WriteSummary(
                            $"Skipped after {authorityTimer.ElapsedMilliseconds} ms because {selectedWeekDescription} returned no new applications to scrape.");
                        continue;
                    }

                    for (var index = 0; index < resultLinks.Count; index++)
                    {
                        queuedApplicationLinks.Add(new QueuedApplicationLink(
                            authority,
                            authorityLog,
                            NormalizeBaseUri(authority.Website!),
                            resultLinks[index],
                            authorityLinkResult.SelectedWeek,
                            weekNumber,
                            index + 1,
                            resultLinks.Count));
                    }

                    logger.LogInformation(
                        "Queued {ApplicationCount} planning applications for {PlanningAuthorityName} from {SelectedWeek}",
                        resultLinks.Count,
                        authority.Name,
                        selectedWeekDescription);
                    authorityLog.WriteSummary(
                        $"Queued {resultLinks.Count} applications from {selectedWeekDescription} for the shuffled scrape queue.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex,
                        "Failed to collect weekly planning links for {PlanningAuthorityName} ({PlanningAuthorityId}) for dropdown weekNumber {WeekNumber} after {ElapsedMilliseconds} ms",
                        authority.Name,
                        authority.Id,
                        weekNumber,
                        authorityTimer.ElapsedMilliseconds);
                    authorityLog.WriteSummary($"FAILED link collection after {authorityTimer.ElapsedMilliseconds} ms for dropdown weekNumber {weekNumber}: {ex.GetType().Name}: {ex.Message}");
                    authorityLog.WriteExceptionSummary(ex);
                    messages.Enqueue($"{authority.Name}: link collection failed for weekNumber {weekNumber} - {ex.Message}");
                }
            }

            if (authoritiesWithSelectedWeek == 0)
            {
                logger.LogInformation(
                    "Stopping weekly planning import scrape because no planning authority selected dropdown weekNumber {WeekNumber}",
                    weekNumber);
                fileLog.WriteRunSummary(
                    $"Stopping weekly planning import scrape because no planning authority selected dropdown weekNumber {weekNumber}.");
                break;
            }

            if (queuedApplicationLinks.Count == 0)
            {
                logger.LogInformation(
                    "No new planning applications were queued for dropdown weekNumber {WeekNumber}",
                    weekNumber);
                fileLog.WriteRunSummary(
                    $"No new planning applications were queued for dropdown weekNumber {weekNumber}.");

                if (scrapeMostRecentWeekOnly)
                {
                    logger.LogInformation("Stopping current week planning import scrape after dropdown weekNumber {WeekNumber}", weekNumber);
                    fileLog.WriteRunSummary($"Stopping current week planning import scrape after dropdown weekNumber {weekNumber}.");
                    break;
                }

                continue;
            }

            var shuffledApplicationLinks = ShuffleApplicationQueueByAuthority(queuedApplicationLinks);
            var authorityStats = queuedApplicationLinks
                .GroupBy(link => link.Authority.Id)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var firstLink = group.First();
                        return new AuthorityWeeklyScrapeStats(
                            firstLink.Authority,
                            firstLink.AuthorityLog,
                            DescribeWeeklySelection(firstLink.SelectedWeek, firstLink.WeekNumber),
                            group.Count());
                    });

            logger.LogInformation(
                "Collected {ApplicationCount} weekly planning application links from {AuthorityCount} authorities for dropdown weekNumber {WeekNumber}; scraping in shuffled order",
                shuffledApplicationLinks.Count,
                authorityStats.Count,
                weekNumber);
            fileLog.WriteRunSummary(
                $"Collected {shuffledApplicationLinks.Count} weekly planning application links from {authorityStats.Count} authorities for dropdown weekNumber {weekNumber}; scraping in shuffled order.");

            var queuedWorkItems = shuffledApplicationLinks
                .Select((link, index) => new QueuedApplicationWorkItem(link, index + 1, shuffledApplicationLinks.Count))
                .ToList();

            await Parallel.ForEachAsync(
                queuedWorkItems,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = applicationImportConcurrency
                },
                async (workItem, workerCancellationToken) =>
            {
                workerCancellationToken.ThrowIfCancellationRequested();

                var queuedLink = workItem.Link;
                var stats = authorityStats[queuedLink.Authority.Id];

                try
                {
                    var application = await ScrapeQueuedApplicationWithBrowserRecoveryAsync(
                        browserSession,
                        queuedLink,
                        workItem.QueueIndex,
                        workItem.QueueCount,
                        messages,
                        documentDownloadThrottle,
                        browserFailureRetryCount,
                        browserRestartDelay,
                        workerCancellationToken);

                    if (application is null)
                    {
                        return;
                    }

                    var applicationDocumentsScraped = application.Documents.Count;
                    lock (scrapeCountersLock)
                    {
                        applicationsScraped++;
                        documentsScraped += applicationDocumentsScraped;
                    }

                    stats.AddScrapeResult(applicationDocumentsScraped);

                    logger.LogInformation(
                        "Importing scraped planning application {ApplicationReference} for {PlanningAuthorityName}",
                        application.ApplicationReference ?? application.SourceKey ?? "unknown application",
                        queuedLink.Authority.Name);
                    queuedLink.AuthorityLog.WriteSummary(
                        $"Importing global application {workItem.QueueIndex}/{workItem.QueueCount}: {DescribeScrapedApplication(application)}.");

                    var saveResult = await SaveApplicationsAsync(queuedLink.Authority.Id, [application], workerCancellationToken);
                    lock (scrapeCountersLock)
                    {
                        applicationsSaved += saveResult.ApplicationsSaved;
                        documentsSaved += saveResult.DocumentsSaved;
                    }

                    stats.AddSaveResult(saveResult);

                    queuedLink.AuthorityLog.WriteSummary(
                        $"Database import finished for global application {workItem.QueueIndex}/{workItem.QueueCount}: applications saved={saveResult.ApplicationsSaved}, documents saved={saveResult.DocumentsSaved}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex,
                        "Failed to scrape or import queued planning application {ApplicationUrl} for {PlanningAuthorityName}",
                        queuedLink.ResultLink.Url,
                        queuedLink.Authority.Name);
                    queuedLink.AuthorityLog.WriteSummary(
                        $"FAILED queued application {queuedLink.AuthorityLinkIndex}/{queuedLink.AuthorityLinkCount} (global {workItem.QueueIndex}/{workItem.QueueCount}): {DescribeSearchResult(queuedLink.ResultLink)}. Error={ex.GetType().Name}: {ex.Message}");
                    queuedLink.AuthorityLog.WriteExceptionSummary(ex);
                    messages.Enqueue($"{queuedLink.Authority.Name}: failed to scrape/import {queuedLink.ResultLink.Url} - {ex.Message}");
                }
            });

            foreach (var stats in authorityStats.Values.OrderBy(stats => stats.Authority.Name))
            {
                logger.LogInformation(
                    "Finished shuffled weekly scrape for {PlanningAuthorityName} from {SelectedWeek}: scraped {ApplicationsScraped}/{QueuedApplications} applications, imported {ApplicationsSaved} applications and {DocumentsSaved} documents",
                    stats.Authority.Name,
                    stats.SelectedWeekDescription,
                    stats.ApplicationsScraped,
                    stats.QueuedApplications,
                    stats.ApplicationsSaved,
                    stats.DocumentsSaved);
                stats.AuthorityLog.WriteSummary(
                    $"Finished shuffled scrape for {stats.SelectedWeekDescription}: scraped {stats.ApplicationsScraped}/{stats.QueuedApplications} applications, documents scraped={stats.DocumentsScraped}, applications saved={stats.ApplicationsSaved}, documents saved={stats.DocumentsSaved}.");
                messages.Enqueue(
                    $"{stats.Authority.Name}: {stats.SelectedWeekDescription} scraped {stats.ApplicationsScraped}/{stats.QueuedApplications} queued applications and {stats.DocumentsScraped} documents.");
            }

            if (scrapeMostRecentWeekOnly)
            {
                logger.LogInformation("Stopping current week planning import scrape after dropdown weekNumber {WeekNumber}", weekNumber);
                fileLog.WriteRunSummary($"Stopping current week planning import scrape after dropdown weekNumber {weekNumber}.");
                break;
            }
        }

        scrapeTimer.Stop();
        logger.LogInformation(
            "Finished {ScrapeDescription} in {ElapsedMilliseconds} ms. Authorities={AuthoritiesProcessed}, ApplicationsScraped={ApplicationsScraped}, ApplicationsSaved={ApplicationsSaved}, DocumentsScraped={DocumentsScraped}, DocumentsSaved={DocumentsSaved}",
            scrapeDescription,
            scrapeTimer.ElapsedMilliseconds,
            authoritiesProcessed,
            applicationsScraped,
            applicationsSaved,
            documentsScraped,
            documentsSaved);
        fileLog.WriteRunSummary(
            $"Finished {scrapeDescription} in {scrapeTimer.ElapsedMilliseconds} ms. Authorities={authoritiesProcessed}, ApplicationsScraped={applicationsScraped}, ApplicationsSaved={applicationsSaved}, DocumentsScraped={documentsScraped}, DocumentsSaved={documentsSaved}");

        return new WeeklyScrapeResult(
            authoritiesProcessed,
            applicationsScraped,
            applicationsSaved,
            documentsScraped,
            documentsSaved,
            startedAt,
            DateTime.UtcNow,
            messages.ToList());
    }

    private async Task<BrowserSession> StartBrowserSessionWithRetryAsync(
        ScraperFileLogger fileLog,
        int retryCount,
        TimeSpan restartDelay,
        CancellationToken cancellationToken)
    {
        var browserSession = new BrowserSession(logger);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await browserSession.StartAsync();
                fileLog.WriteRunSummary("Launched headless Chromium for weekly planning import scrape.");

                return browserSession;
            }
            catch (Exception ex) when (IsRestartableBrowserFailure(ex) && attempt < retryCount)
            {
                await browserSession.DisposeAsync();

                logger.LogWarning(
                    ex,
                    "Failed to launch Playwright/Chromium. Waiting {RestartDelaySeconds} seconds before retry {RetryAttempt}/{RetryCount}",
                    restartDelay.TotalSeconds,
                    attempt + 1,
                    retryCount);
                fileLog.WriteRunSummary(
                    $"Failed to launch Playwright/Chromium ({ex.GetType().Name}: {ex.Message}). Waiting {restartDelay.TotalSeconds:N0} seconds before retry {attempt + 1}/{retryCount}.");

                await Task.Delay(restartDelay, cancellationToken);
            }
            catch
            {
                await browserSession.DisposeAsync();
                throw;
            }
        }
    }

    private async Task<AuthorityApplicationLinkResult> CollectAuthorityApplicationLinksWithBrowserRecoveryAsync(
        BrowserSession browserSession,
        PlanningAuthority authority,
        AuthorityFileLog authorityLog,
        ConcurrentQueue<string> messages,
        int weekNumber,
        int retryCount,
        TimeSpan restartDelay,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var browserSnapshot = await browserSession.GetSnapshotAsync(cancellationToken);
            try
            {
                return await CollectAuthorityApplicationLinksAsync(browserSnapshot.Browser, authority, authorityLog, messages, weekNumber, cancellationToken);
            }
            catch (Exception ex) when (IsRestartableBrowserFailure(ex) && attempt < retryCount)
            {
                logger.LogWarning(
                    ex,
                    "Playwright/Chromium failed while collecting links for {PlanningAuthorityName}. Restarting browser and retrying authority after {RestartDelaySeconds} seconds ({RetryAttempt}/{RetryCount})",
                    authority.Name,
                    restartDelay.TotalSeconds,
                    attempt + 1,
                    retryCount);
                authorityLog.WriteSummary(
                    $"Playwright/Chromium failed while collecting links ({ex.GetType().Name}: {ex.Message}). Stopping browser, waiting {restartDelay.TotalSeconds:N0} seconds, then retrying authority ({attempt + 1}/{retryCount}).");
                messages.Enqueue(
                    $"{authority.Name}: browser failed while collecting links; restarting Playwright/Chromium and retrying after {restartDelay.TotalSeconds:N0} seconds.");

                await browserSession.RestartAsync(restartDelay, cancellationToken);
            }
        }
    }

    private async Task<AuthorityApplicationLinkResult> CollectAuthorityApplicationLinksAsync(
        IBrowser browser,
        PlanningAuthority authority,
        AuthorityFileLog authorityLog,
        ConcurrentQueue<string> messages,
        int weekNumber,
        CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(authority.Website!);
        authorityLog.WriteSummary($"Base URI: {baseUri}");

        logger.LogDebug("Opening browser context for {PlanningAuthorityName} using base URI {BaseUri}", authority.Name, baseUri);
        authorityLog.WriteSummary("Opening browser context.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(scraperOptions.Value.DefaultTimeoutMilliseconds);
        page.SetDefaultNavigationTimeout(scraperOptions.Value.DefaultNavigationTimeoutMilliseconds);

        logger.LogDebug("Navigating to planning authority homepage {BaseUri}", baseUri);
        authorityLog.WriteSummary($"Navigating to homepage: {baseUri}");
        await NavigateWithTooManyRequestsRetryAsync(
            page,
            baseUri.ToString(),
            $"homepage for {authority.Name}",
            logger,
            authorityLog.WriteSummary,
            cancellationToken);
        authorityLog.WriteSummary($"Homepage loaded: {page.Url}");

        logger.LogDebug("Opening weekly list for {PlanningAuthorityName}", authority.Name);
        authorityLog.WriteSummary("Opening weekly list.");
        await OpenWeeklyListAsync(page, baseUri, logger, authority, authorityLog, cancellationToken);
        authorityLog.WriteSummary($"Weekly list page loaded: {page.Url}");

        // The portal preselects its latest week, so weekNumber 0 preserves the old behavior.
        var selectedWeekResult = await TrySelectWeekByNumberAsync(page, weekNumber);
        if (!selectedWeekResult.WeekSelectFound)
        {
            logger.LogWarning(
                "{PlanningAuthorityName}: weekly list page did not contain the #week dropdown at {WeeklyListUrl}; reopening weekly list once",
                authority.Name,
                page.Url);
            authorityLog.WriteSummary(
                $"Weekly list page did not contain the #week dropdown at {page.Url}; reopening weekly list once before failing.");

            await Task.Delay(1000, cancellationToken);
            await OpenWeeklyListAsync(page, baseUri, logger, authority, authorityLog, cancellationToken);
            authorityLog.WriteSummary($"Weekly list page reloaded: {page.Url}");
            selectedWeekResult = await TrySelectWeekByNumberAsync(page, weekNumber);
        }

        if (!selectedWeekResult.WeekSelectFound)
        {
            logger.LogWarning(
                "{PlanningAuthorityName}: weekly list page did not contain the #week dropdown at {WeeklyListUrl}",
                authority.Name,
                page.Url);
            authorityLog.WriteSummary(
                $"Weekly list page did not contain the #week dropdown at {page.Url}; treating this as a page-load failure rather than the end of the weekly dropdown.");

            throw new InvalidOperationException($"Weekly list page did not contain the #week dropdown at {page.Url}.");
        }

        var selectedWeek = selectedWeekResult.SelectedWeek;
        if (selectedWeek is null)
        {
            logger.LogInformation(
                "{PlanningAuthorityName}: weekly dropdown has {OptionCount} options and no option for weekNumber {WeekNumber}",
                authority.Name,
                selectedWeekResult.OptionCount,
                weekNumber);
            authorityLog.WriteSummary(
                $"Weekly dropdown has {selectedWeekResult.OptionCount} options and no option for weekNumber {weekNumber}; skipping authority for this pass.");
            messages.Enqueue($"{authority.Name}: weekly dropdown has no option for weekNumber {weekNumber}.");

            return new AuthorityApplicationLinkResult([], Skipped: true, WeekUnavailable: true, SelectedWeek: null);
        }

        authorityLog.WriteSummary($"Selected weekly list: {DescribeWeeklySelection(selectedWeek, weekNumber)}.");

        logger.LogDebug("Submitting weekly list search for {PlanningAuthorityName}", authority.Name);
        authorityLog.WriteSummary("Submitting weekly list search.");
        await SubmitWeeklySearchAsync(page, logger, authority, authorityLog, cancellationToken);
        authorityLog.WriteSummary($"Weekly list search submitted; results page: {page.Url}");
        await TrySetResultsPerPageToMaximumAsync(page, logger, authority, authorityLog, cancellationToken);

        var resultLinks = await CollectResultLinksAsync(page, baseUri, logger, authority, authorityLog, cancellationToken);

        var filterResult = await FilterPreviouslyScrapedApplicationsAsync(resultLinks, authority, cancellationToken);
        if (filterResult.SkippedApplications.Count > 0)
        {
            authorityLog.WriteSummary($"Skipped {filterResult.SkippedLinkCount} weekly result links that already exist in the database.");
            foreach (var skippedApplication in filterResult.SkippedApplications)
            {
                authorityLog.WriteSummary($"Previously scraped: {skippedApplication}");
            }
        }

        logger.LogInformation(
            "Collected {ResultCount} weekly planning result links for {PlanningAuthorityName}",
            resultLinks.Count,
            authority.Name);
        authorityLog.WriteSummary($"Collected {resultLinks.Count} weekly planning result links to scrape after filtering.");

        for (var index = 0; index < resultLinks.Count; index++)
        {
            var resultLink = resultLinks[index];
            authorityLog.WriteSummary(
                $"Result {index + 1}/{resultLinks.Count}: {DescribeSearchResult(resultLink)}");
        }

        if (resultLinks.Count == 0)
        {
            logger.LogInformation(
                "{PlanningAuthorityName}: {SelectedWeek} returned no new applications",
                authority.Name,
                DescribeWeeklySelection(selectedWeek, weekNumber));
            authorityLog.WriteSummary($"{DescribeWeeklySelection(selectedWeek, weekNumber)} returned no new applications.");
            messages.Enqueue($"{authority.Name}: {DescribeWeeklySelection(selectedWeek, weekNumber)} returned no new applications");

            return new AuthorityApplicationLinkResult(resultLinks, Skipped: true, WeekUnavailable: false, SelectedWeek: selectedWeek);
        }

        return new AuthorityApplicationLinkResult(resultLinks, Skipped: false, WeekUnavailable: false, SelectedWeek: selectedWeek);
    }

    private async Task<ScrapedApplication?> ScrapeQueuedApplicationWithBrowserRecoveryAsync(
        BrowserSession browserSession,
        QueuedApplicationLink queuedLink,
        int queueIndex,
        int queueCount,
        ConcurrentQueue<string> messages,
        SemaphoreSlim documentDownloadThrottle,
        int retryCount,
        TimeSpan restartDelay,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var browserSnapshot = await browserSession.GetSnapshotAsync(cancellationToken);
            try
            {
                return await ScrapeQueuedApplicationAsync(
                    browserSnapshot.Browser,
                    queuedLink,
                    queueIndex,
                    queueCount,
                    messages,
                    documentDownloadThrottle,
                    cancellationToken);
            }
            catch (Exception ex) when (IsRestartableBrowserFailure(ex) && attempt < retryCount)
            {
                logger.LogWarning(
                    ex,
                    "Playwright/Chromium failed while scraping queued planning application {ApplicationUrl} for {PlanningAuthorityName}. Restarting browser after {RestartDelaySeconds} seconds ({RetryAttempt}/{RetryCount})",
                    queuedLink.ResultLink.Url,
                    queuedLink.Authority.Name,
                    restartDelay.TotalSeconds,
                    attempt + 1,
                    retryCount);
                queuedLink.AuthorityLog.WriteSummary(
                    $"Playwright/Chromium failed while scraping queued application {queuedLink.AuthorityLinkIndex}/{queuedLink.AuthorityLinkCount} (global {queueIndex}/{queueCount}): {ex.GetType().Name}: {ex.Message}. Restarting browser after {restartDelay.TotalSeconds:N0} seconds ({attempt + 1}/{retryCount}).");
                messages.Enqueue(
                    $"{queuedLink.Authority.Name}: browser failed while scraping {queuedLink.ResultLink.Url}; restarting Playwright/Chromium and retrying after {restartDelay.TotalSeconds:N0} seconds.");

                await browserSession.RestartAsync(restartDelay, cancellationToken, browserSnapshot.Generation);
            }
            catch (Exception ex) when (IsRestartableBrowserFailure(ex))
            {
                logger.LogError(
                    ex,
                    "Playwright/Chromium failed while scraping queued planning application {ApplicationUrl} for {PlanningAuthorityName}; retry limit reached",
                    queuedLink.ResultLink.Url,
                    queuedLink.Authority.Name);
                queuedLink.AuthorityLog.WriteSummary(
                    $"FAILED queued application {queuedLink.AuthorityLinkIndex}/{queuedLink.AuthorityLinkCount} (global {queueIndex}/{queueCount}) after browser retry limit: {DescribeSearchResult(queuedLink.ResultLink)}. Error={ex.GetType().Name}: {ex.Message}");
                queuedLink.AuthorityLog.WriteExceptionSummary(ex);
                messages.Enqueue($"{queuedLink.Authority.Name}: browser failed while scraping {queuedLink.ResultLink.Url} after retries - {ex.Message}");

                await browserSession.RestartAsync(restartDelay, cancellationToken, browserSnapshot.Generation);
                return null;
            }
        }
    }

    private async Task<ScrapedApplication?> ScrapeQueuedApplicationAsync(
        IBrowser browser,
        QueuedApplicationLink queuedLink,
        int queueIndex,
        int queueCount,
        ConcurrentQueue<string> messages,
        SemaphoreSlim documentDownloadThrottle,
        CancellationToken cancellationToken)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(scraperOptions.Value.DefaultTimeoutMilliseconds);
        page.SetDefaultNavigationTimeout(scraperOptions.Value.DefaultNavigationTimeoutMilliseconds);

        var applicationTimer = Stopwatch.StartNew();
        var applicationLog = queuedLink.AuthorityLog.CreateApplicationLog(
            queuedLink.ResultLink,
            queuedLink.AuthorityLinkIndex,
            queuedLink.AuthorityLinkCount);
        queuedLink.AuthorityLog.WriteSummary(
            $"Application {queuedLink.AuthorityLinkIndex}/{queuedLink.AuthorityLinkCount} (global {queueIndex}/{queueCount}) started from shuffled queue: {DescribeSearchResult(queuedLink.ResultLink)}. Detail log: {applicationLog.FileName}");

        try
        {
            logger.LogInformation(
                "Scraping queued planning application {ApplicationIndex}/{ApplicationCount} for {PlanningAuthorityName}: {ApplicationUrl}",
                queueIndex,
                queueCount,
                queuedLink.Authority.Name,
                queuedLink.ResultLink.Url);

            var application = await ScrapeApplicationAsync(
                page,
                queuedLink.BaseUri,
                queuedLink.ResultLink,
                applicationLog,
                documentDownloadThrottle,
                cancellationToken);
            applicationTimer.Stop();
            applicationLog.WriteSection("Finished");
            applicationLog.WriteLine($"Status: succeeded in {applicationTimer.ElapsedMilliseconds} ms.");
            queuedLink.AuthorityLog.WriteSummary(
                $"Application {queuedLink.AuthorityLinkIndex}/{queuedLink.AuthorityLinkCount} (global {queueIndex}/{queueCount}) succeeded in {applicationTimer.ElapsedMilliseconds} ms: {DescribeScrapedApplication(application)}. Documents={application.Documents.Count}. Detail log: {applicationLog.FileName}");

            return application;
        }
        catch (Exception ex) when (IsRestartableBrowserFailure(ex))
        {
            applicationTimer.Stop();
            applicationLog.WriteSection("Browser Failure");
            applicationLog.WriteLine($"Status: browser failed in {applicationTimer.ElapsedMilliseconds} ms.");
            applicationLog.WriteException(ex);
            queuedLink.AuthorityLog.WriteSummary(
                $"Application {queuedLink.AuthorityLinkIndex}/{queuedLink.AuthorityLinkCount} (global {queueIndex}/{queueCount}) stopped because Playwright/Chromium failed in {applicationTimer.ElapsedMilliseconds} ms: {DescribeSearchResult(queuedLink.ResultLink)}. Error={ex.GetType().Name}: {ex.Message}. Detail log: {applicationLog.FileName}");

            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            applicationTimer.Stop();
            applicationLog.WriteSection("Failed");
            applicationLog.WriteLine($"Status: failed in {applicationTimer.ElapsedMilliseconds} ms.");
            applicationLog.WriteLine($"Page at failure: {page.Url}");
            applicationLog.WriteException(ex);
            queuedLink.AuthorityLog.WriteSummary(
                $"Application {queuedLink.AuthorityLinkIndex}/{queuedLink.AuthorityLinkCount} (global {queueIndex}/{queueCount}) FAILED in {applicationTimer.ElapsedMilliseconds} ms: {DescribeSearchResult(queuedLink.ResultLink)}. Page at failure: {page.Url}. Error={ex.GetType().Name}: {ex.Message}. Detail log: {applicationLog.FileName}");
            logger.LogWarning(
                ex,
                "{PlanningAuthorityName}: failed to scrape queued planning application {ApplicationUrl}",
                queuedLink.Authority.Name,
                queuedLink.ResultLink.Url);
            messages.Enqueue($"{queuedLink.Authority.Name}: failed to scrape {queuedLink.ResultLink.Url} - {ex.Message}");

            return null;
        }
    }

    private async Task<PreviouslyScrapedFilterResult> FilterPreviouslyScrapedApplicationsAsync(
        List<SearchResultDto> resultLinks,
        PlanningAuthority authority,
        CancellationToken cancellationToken)
    {
        var sourceKeys = resultLinks
            .Select(result => ExtractQueryValue(result.Url, "keyVal"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applicationReferences = resultLinks
            .Select(result => ExtractReferenceFromMeta(result.Meta))
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sourceKeys.Count == 0 && applicationReferences.Count == 0)
        {
            return PreviouslyScrapedFilterResult.Empty;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var previouslyScrapedQuery = db.PlanningApplications
            .AsNoTracking()
            .Where(application => application.PlanningAuthorityId == authority.Id);

        if (sourceKeys.Count > 0 && applicationReferences.Count > 0)
        {
            previouslyScrapedQuery = previouslyScrapedQuery.Where(application =>
                (application.SourceKey != null && sourceKeys.Contains(application.SourceKey))
                || (application.ApplicationReference != null && applicationReferences.Contains(application.ApplicationReference)));
        }
        else if (sourceKeys.Count > 0)
        {
            previouslyScrapedQuery = previouslyScrapedQuery.Where(application =>
                application.SourceKey != null && sourceKeys.Contains(application.SourceKey));
        }
        else
        {
            previouslyScrapedQuery = previouslyScrapedQuery.Where(application =>
                application.ApplicationReference != null && applicationReferences.Contains(application.ApplicationReference));
        }

        var previouslyScrapedApplications = await previouslyScrapedQuery
            .Select(application => new
            {
                application.SourceKey,
                application.ApplicationReference,
                application.Title,
                DocumentCount = application.PlanningDocuments.Count
            })
            .ToListAsync(cancellationToken);

        if (previouslyScrapedApplications.Count == 0)
        {
            return PreviouslyScrapedFilterResult.Empty;
        }

        var previouslyScrapedSourceKeys = previouslyScrapedApplications
            .Select(application => application.SourceKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previouslyScrapedApplicationReferences = previouslyScrapedApplications
            .Select(application => application.ApplicationReference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedCount = resultLinks.RemoveAll(result =>
        {
            var sourceKey = ExtractQueryValue(result.Url, "keyVal");
            var applicationReference = ExtractReferenceFromMeta(result.Meta);

            return (!string.IsNullOrWhiteSpace(sourceKey)
                    && previouslyScrapedSourceKeys.Contains(sourceKey))
                || (!string.IsNullOrWhiteSpace(applicationReference)
                    && previouslyScrapedApplicationReferences.Contains(applicationReference));
        });

        var skippedApplications = previouslyScrapedApplications
            .OrderBy(application => application.ApplicationReference ?? application.SourceKey)
            .Select(application =>
            {
                var identifiers = new List<string>();
                if (!string.IsNullOrWhiteSpace(application.ApplicationReference))
                {
                    identifiers.Add($"reference={application.ApplicationReference}");
                }

                if (!string.IsNullOrWhiteSpace(application.SourceKey))
                {
                    identifiers.Add($"sourceKey={application.SourceKey}");
                }

                identifiers.Add($"{application.DocumentCount} documents");

                return $"{FirstNonEmpty(application.ApplicationReference, application.Title, application.SourceKey)} ({string.Join(", ", identifiers)})";
            })
            .ToList();

        logger.LogInformation(
            "{PlanningAuthorityName}: skipped {SkippedLinkCount} weekly result links for {PreviouslyScrapedApplicationCount} existing planning applications: {PreviouslyScrapedApplications}",
            authority.Name,
            removedCount,
            previouslyScrapedApplications.Count,
            string.Join("; ", skippedApplications));

        return new PreviouslyScrapedFilterResult(removedCount, skippedApplications);
    }

    private static List<QueuedApplicationLink> ShuffleApplicationQueueByAuthority(
        IReadOnlyList<QueuedApplicationLink> applicationLinks)
    {
        var authorityQueues = applicationLinks
            .GroupBy(link => link.Authority.Id)
            .Select(group =>
            {
                var links = group.ToList();
                ShuffleInPlace(links);
                return new Queue<QueuedApplicationLink>(links);
            })
            .ToList();
        var shuffled = new List<QueuedApplicationLink>(applicationLinks.Count);
        Guid? previousAuthorityId = null;

        while (authorityQueues.Count > 0)
        {
            var candidates = authorityQueues
                .Where(queue => queue.Peek().Authority.Id != previousAuthorityId)
                .ToList();
            if (candidates.Count == 0)
            {
                candidates = authorityQueues;
            }

            var selectedQueue = candidates[Random.Shared.Next(candidates.Count)];
            var nextLink = selectedQueue.Dequeue();
            shuffled.Add(nextLink);
            previousAuthorityId = nextLink.Authority.Id;

            if (selectedQueue.Count == 0)
            {
                authorityQueues.Remove(selectedQueue);
            }
        }

        return shuffled;
    }

    private static void ShuffleInPlace<T>(IList<T> items)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }


    private static async Task OpenWeeklyListAsync(
        IPage page,
        Uri baseUri,
        ILogger logger,
        PlanningAuthority authority,
        AuthorityFileLog authorityLog,
        CancellationToken cancellationToken)
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
                await NavigateWithTooManyRequestsRetryAsync(
                    page,
                    weeklyUri.ToString(),
                    $"weekly list for {authority.Name}",
                    logger,
                    authorityLog.WriteSummary,
                    cancellationToken);

                return;
            }
        }

        var weeklyListUri = new Uri(baseUri, "search.do?action=weeklyList&searchType=Application");
        await NavigateWithTooManyRequestsRetryAsync(
            page,
            weeklyListUri.ToString(),
            $"weekly list for {authority.Name}",
            logger,
            authorityLog.WriteSummary,
            cancellationToken);
    }

    private static async Task SubmitWeeklySearchAsync(
        IPage page,
        ILogger logger,
        PlanningAuthority authority,
        AuthorityFileLog authorityLog,
        CancellationToken cancellationToken)
    {
        await ClickAndWaitForLoadStateWithTooManyRequestsRetryAsync(
            page,
            page.Locator("#weeklyListForm input[type='submit'], input[type='submit'][value='Search']").First,
            $"weekly search results for {authority.Name}",
            logger,
            authorityLog.WriteSummary,
            cancellationToken);
    }

    private static async Task<WeeklyListSelectionResult> TrySelectWeekByNumberAsync(IPage page, int weekNumber)
    {
        var weekSelect = page.Locator("#week").First;

        try
        {
            await weekSelect.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 5000
            });
        }
        catch (TimeoutException)
        {
            return new WeeklyListSelectionResult(false, 0, null);
        }

        var json = await weekSelect.EvaluateAsync<string>(
            """
            (select, weekNumber) => {
                const options = Array.from(select.options || []);
                const targetIndex = Number(weekNumber);

                if (!Number.isInteger(targetIndex) || targetIndex < 0 || targetIndex >= options.length) {
                    return JSON.stringify({
                        found: false,
                        optionCount: options.length,
                        targetIndex
                    });
                }

                const selectedOption = options[targetIndex];
                select.selectedIndex = targetIndex;
                selectedOption.selected = true;
                select.dispatchEvent(new Event('input', { bubbles: true }));
                select.dispatchEvent(new Event('change', { bubbles: true }));

                return JSON.stringify({
                    found: true,
                    optionCount: options.length,
                    targetIndex,
                    value: select.value || selectedOption.value || '',
                    label: selectedOption.textContent || ''
                });
            }
            """,
            weekNumber);
        var selectedWeekDto = JsonSerializer.Deserialize<SelectedWeekDto>(json, JsonOptions);
        if (selectedWeekDto is not { Found: true })
        {
            return new WeeklyListSelectionResult(
                true,
                selectedWeekDto?.OptionCount ?? 0,
                null);
        }

        var weekStart = TryParseWeekStart(selectedWeekDto.Value, selectedWeekDto.Label);

        return new WeeklyListSelectionResult(
            true,
            selectedWeekDto.OptionCount,
            new WeeklyListSelection(
                selectedWeekDto.TargetIndex,
                selectedWeekDto.Value,
                selectedWeekDto.Label,
                weekStart));
    }

    private static string DescribeWeeklySelection(WeeklyListSelection? selectedWeek, int weekNumber)
    {
        if (selectedWeek is null)
        {
            return $"weekNumber {weekNumber}";
        }

        var label = NormalizeText(selectedWeek.Label) ?? "(no label)";
        var value = NormalizeText(selectedWeek.Value) ?? "(empty value)";
        var weekStart = selectedWeek.WeekStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";

        return $"weekNumber {weekNumber}, dropdown index {selectedWeek.DropdownIndex}: {label} (value={value}, week start={weekStart})";
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

    private static async Task TrySetResultsPerPageToMaximumAsync(
        IPage page,
        ILogger logger,
        PlanningAuthority authority,
        AuthorityFileLog authorityLog,
        CancellationToken cancellationToken)
    {
        var select = page.Locator("#resultsPerPage");

        if (await select.CountAsync() == 0)
        {
            logger.LogDebug("No results per page selector found for {PlanningAuthorityName}", authority.Name);
            authorityLog.WriteSummary("No results-per-page selector found; keeping the portal default page size.");
            return;
        }

        try
        {
            await select.SelectOptionAsync("100");
            authorityLog.WriteSummary("Selected 100 results per page.");

            var submit = page.Locator("#searchResults input[type='submit'], #searchfilters input[type='submit']").First;
            if (await submit.CountAsync() > 0)
            {
                await ClickAndWaitForLoadStateWithTooManyRequestsRetryAsync(
                    page,
                    submit,
                    $"maximum-results search results for {authority.Name}",
                    logger,
                    authorityLog.WriteSummary,
                    cancellationToken);
                authorityLog.WriteSummary($"Results-per-page form submitted; page loaded: {page.Url}");
            }
        }
        catch (PlaywrightException ex)
            when (IsRestartableBrowserFailure(ex))
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            logger.LogDebug("No results per page selector found for {PlanningAuthorityName}", authority.Name);
            authorityLog.WriteSummary($"Could not select 100 results per page: {ex.Message}");
        }
    }

    private static async Task<IResponse?> NavigateWithTooManyRequestsRetryAsync(
        IPage page,
        string url,
        string description,
        ILogger logger,
        Action<string>? writeLog,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });

            if (!IsTooManyRequestsResponse(response))
            {
                return response;
            }

            if (attempt >= TooManyRequestsRetryCount)
            {
                throw CreateTooManyRequestsException(response!, description);
            }

            await WaitBeforeTooManyRequestsRetryAsync(response!, description, attempt, logger, writeLog, cancellationToken);
        }
    }

    private static async Task ClickAndWaitForLoadStateWithTooManyRequestsRetryAsync(
        IPage page,
        ILocator locator,
        string description,
        ILogger logger,
        Action<string>? writeLog,
        CancellationToken cancellationToken)
    {
        IResponse? tooManyRequestsResponse = null;

        void OnResponse(object? _, IResponse response)
        {
            if (IsTooManyRequestsResponse(response)
                && response.Frame == page.MainFrame
                && response.Request.ResourceType.Equals("document", StringComparison.OrdinalIgnoreCase))
            {
                tooManyRequestsResponse = response;
            }
        }

        page.Response += OnResponse;
        try
        {
            await locator.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
        finally
        {
            page.Response -= OnResponse;
        }

        if (tooManyRequestsResponse is not null)
        {
            await ReloadAfterTooManyRequestsAsync(
                page,
                tooManyRequestsResponse,
                description,
                logger,
                writeLog,
                cancellationToken);
        }
    }

    private static async Task ReloadAfterTooManyRequestsAsync(
        IPage page,
        IResponse response,
        string description,
        ILogger logger,
        Action<string>? writeLog,
        CancellationToken cancellationToken)
    {
        IResponse? currentResponse = response;

        for (var attempt = 0; ; attempt++)
        {
            if (attempt >= TooManyRequestsRetryCount)
            {
                throw CreateTooManyRequestsException(currentResponse!, description);
            }

            await WaitBeforeTooManyRequestsRetryAsync(currentResponse!, description, attempt, logger, writeLog, cancellationToken);

            currentResponse = await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });

            if (!IsTooManyRequestsResponse(currentResponse))
            {
                return;
            }
        }
    }

    private static bool IsTooManyRequestsResponse(IResponse? response)
    {
        return response?.Status == (int)HttpStatusCode.TooManyRequests;
    }

    private static async Task WaitBeforeTooManyRequestsRetryAsync(
        IResponse response,
        string description,
        int attempt,
        ILogger logger,
        Action<string>? writeLog,
        CancellationToken cancellationToken)
    {
        var retryDelay = await GetTooManyRequestsRetryDelayAsync(response);

        logger.LogWarning(
            "Planning portal navigation was rate limited with HTTP 429 for {NavigationDescription} at {NavigationUrl}. Waiting {RetryDelaySeconds} seconds before retry {RetryAttempt}/{RetryCount}",
            description,
            response.Url,
            retryDelay.TotalSeconds,
            attempt + 1,
            TooManyRequestsRetryCount);
        writeLog?.Invoke(
            $"HTTP 429 rate limit while loading {description} at {response.Url}. Waiting {retryDelay.TotalSeconds:N0} seconds before retry {attempt + 1}/{TooManyRequestsRetryCount}.");

        await Task.Delay(retryDelay, cancellationToken);
    }

    private static async Task<TimeSpan> GetTooManyRequestsRetryDelayAsync(IResponse response)
    {
        var retryAfter = await response.HeaderValueAsync("retry-after");
        return GetTooManyRequestsRetryDelay(retryAfter);
    }

    private static TimeSpan GetTooManyRequestsRetryDelay(string? retryAfter)
    {
        if (!string.IsNullOrWhiteSpace(retryAfter)
            && RetryConditionHeaderValue.TryParse(retryAfter, out var retryAfterHeader))
        {
            if (retryAfterHeader.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (retryAfterHeader.Date is { } retryDate)
            {
                var delay = retryDate - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    return delay;
                }
            }
        }

        return DefaultTooManyRequestsRetryDelay;
    }

    private static InvalidOperationException CreateTooManyRequestsException(
        IResponse response,
        string description)
    {
        return new InvalidOperationException(
            $"Planning portal navigation was rate limited with HTTP 429 for {description} at {response.Url} after {TooManyRequestsRetryCount} retry.");
    }

    private static async Task<List<SearchResultDto>> CollectResultLinksAsync(
        IPage page,
        Uri baseUri,
        ILogger logger,
        PlanningAuthority authority,
        AuthorityFileLog authorityLog,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResultDto>();
        var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            visitedPages.Add(page.Url);
            var pageResults = await ExtractSearchResultsAsync(page);
            results.AddRange(pageResults);
            authorityLog.WriteSummary(
                $"Search results page {visitedPages.Count}: extracted {pageResults.Count} result links from {page.Url}");

            var next = page.Locator("p.pager.bottom a.next, p.pager.top a.next").First;
            if (await next.CountAsync() == 0)
            {
                logger.LogDebug("No next page link found for {PlanningAuthorityName}", authority.Name);
                authorityLog.WriteSummary("No next search results page link found.");
                break;
            }

            var nextHref = await next.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(nextHref))
            {
                authorityLog.WriteSummary("Next search results page link did not contain an href.");
                break;
            }

            var nextUrl = new Uri(baseUri, WebUtility.HtmlDecode(nextHref)).ToString();
            if (!visitedPages.Add(nextUrl))
            {
                authorityLog.WriteSummary($"Stopping search result pagination because {nextUrl} was already visited.");
                break;
            }

            authorityLog.WriteSummary($"Opening next search results page: {nextUrl}");
            await NavigateWithTooManyRequestsRetryAsync(
                page,
                nextUrl,
                $"next search results page for {authority.Name}",
                logger,
                authorityLog.WriteSummary,
                cancellationToken);
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
        ApplicationFileLog applicationLog,
        SemaphoreSlim documentDownloadThrottle,
        CancellationToken cancellationToken)
    {
        var applicationUrl = new Uri(baseUri, WebUtility.HtmlDecode(resultLink.Url)).ToString();
        var sourceKey = ExtractQueryValue(applicationUrl, "keyVal");
        var summaryUrl = BuildApplicationTabUrl(applicationUrl, "summary");

        applicationLog.WriteSection("Input");
        applicationLog.WriteKeyValue("Weekly result title", resultLink.Title);
        applicationLog.WriteKeyValue("Weekly result address", resultLink.Address);
        applicationLog.WriteKeyValue("Weekly result meta", resultLink.Meta);
        applicationLog.WriteKeyValue("Application URL", applicationUrl);
        applicationLog.WriteKeyValue("Source key", sourceKey);

        logger.LogDebug("Opening summary tab for planning application {ApplicationUrl}", applicationUrl);
        applicationLog.WriteSection("Summary Tab");
        applicationLog.WriteKeyValue("Navigating to", summaryUrl);
        await NavigateWithTooManyRequestsRetryAsync(
            page,
            summaryUrl,
            $"summary tab for planning application {FirstNonEmpty(resultLink.Title, sourceKey, applicationUrl)}",
            logger,
            applicationLog.WriteLine,
            cancellationToken);
        applicationLog.WriteKeyValue("Loaded URL", page.Url);

        var details = await ExtractDetailsAsync(page);
        applicationLog.WriteDictionary("Summary Details", details);
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
        applicationLog.WriteApplicationFields(application);

        logger.LogInformation(
            "Scraping planning application {ApplicationReference} ({SourceKey}) from {ApplicationUrl}",
            application.ApplicationReference ?? "unknown reference",
            application.SourceKey ?? "unknown key",
            applicationUrl);

        logger.LogDebug("Opening contacts tab for planning application {ApplicationReference}", application.ApplicationReference ?? application.SourceKey);
        var contactsUrl = BuildApplicationTabUrl(applicationUrl, "contacts");
        applicationLog.WriteSection("Contacts Tab");
        applicationLog.WriteKeyValue("Navigating to", contactsUrl);
        await NavigateWithTooManyRequestsRetryAsync(
            page,
            contactsUrl,
            $"contacts tab for planning application {application.ApplicationReference ?? application.SourceKey ?? applicationUrl}",
            logger,
            applicationLog.WriteLine,
            cancellationToken);
        applicationLog.WriteKeyValue("Loaded URL", page.Url);
        var contacts = await ExtractContactsAsync(page);
        applicationLog.WriteContacts(contacts);
        ApplyContacts(application, contacts);
        applicationLog.WriteSection("Mapped Contact Fields");
        applicationLog.WriteKeyValue("Applicant name", application.ApplicantName);
        applicationLog.WriteKeyValue("Applicant email", application.ApplicantEmail);
        applicationLog.WriteKeyValue("Applicant phone", application.ApplicantPhone);
        applicationLog.WriteKeyValue("Agent name", application.AgentName);
        applicationLog.WriteKeyValue("Agent email", application.AgentEmail);
        applicationLog.WriteKeyValue("Agent phone", application.AgentPhone);

        logger.LogDebug("Opening details tab for planning application {ApplicationReference}", application.ApplicationReference ?? application.SourceKey);
        var detailsUrl = BuildApplicationTabUrl(applicationUrl, "details");
        applicationLog.WriteSection("Details Tab");
        applicationLog.WriteKeyValue("Navigating to", detailsUrl);
        await NavigateWithTooManyRequestsRetryAsync(
            page,
            detailsUrl,
            $"details tab for planning application {application.ApplicationReference ?? application.SourceKey ?? applicationUrl}",
            logger,
            applicationLog.WriteLine,
            cancellationToken);
        applicationLog.WriteKeyValue("Loaded URL", page.Url);
        var furtherInformation = await ExtractFurtherInformationAsync(page);
        applicationLog.WriteDictionary("Further Information Fields", furtherInformation.Fields);
        ApplyFurtherInformation(application, furtherInformation);
        applicationLog.WriteKeyValue("Company name", application.CompanyName);

        logger.LogDebug("Opening documents tab for planning application {ApplicationReference}", application.ApplicationReference ?? application.SourceKey);
        var documentsUrl = BuildApplicationTabUrl(applicationUrl, "documents");
        applicationLog.WriteSection("Documents Tab");
        applicationLog.WriteKeyValue("Navigating to", documentsUrl);
        await NavigateWithTooManyRequestsRetryAsync(
            page,
            documentsUrl,
            $"documents tab for planning application {application.ApplicationReference ?? application.SourceKey ?? applicationUrl}",
            logger,
            applicationLog.WriteLine,
            cancellationToken);
        applicationLog.WriteKeyValue("Loaded URL", page.Url);
        var documents = await ExtractDocumentsAsync(page);
        applicationLog.WriteLine($"Found {documents.Count} document rows.");
        logger.LogInformation(
            "Found {DocumentCount} documents for planning application {ApplicationReference}",
            documents.Count,
            application.ApplicationReference ?? application.SourceKey ?? applicationUrl);

        var extractedDocuments = await ExtractDocumentsConcurrentlyAsync(
            documents,
            page.Context,
            application,
            applicationUrl,
            applicationLog,
            documentDownloadThrottle,
            cancellationToken);
        foreach (var document in extractedDocuments)
        {
            application.Documents.Add(document);
        }

        applicationLog.WriteSection("Scraped Application Result");
        applicationLog.WriteApplicationFields(application);
        applicationLog.WriteKeyValue("Documents added", application.Documents.Count);

        return application;
    }

    private async Task<IReadOnlyList<ScrapedDocument>> ExtractDocumentsConcurrentlyAsync(
        IReadOnlyList<ScrapedDocument> documents,
        IBrowserContext browserContext,
        ScrapedApplication application,
        string applicationUrl,
        ApplicationFileLog applicationLog,
        SemaphoreSlim documentDownloadThrottle,
        CancellationToken cancellationToken)
    {
        var extractionTasks = documents
            .Select((document, index) => ExtractDocumentAsync(
                document,
                index + 1,
                documents.Count,
                browserContext,
                application,
                applicationUrl,
                applicationLog,
                documentDownloadThrottle,
                cancellationToken))
            .ToArray();
        var extractionResults = await Task.WhenAll(extractionTasks);

        return extractionResults
            .OrderBy(result => result.Index)
            .Select(result => result.Document)
            .ToList();
    }

    private async Task<ScrapedDocumentExtractionResult> ExtractDocumentAsync(
        ScrapedDocument document,
        int index,
        int total,
        IBrowserContext browserContext,
        ScrapedApplication application,
        string applicationUrl,
        ApplicationFileLog applicationLog,
        SemaphoreSlim documentDownloadThrottle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        applicationLog.WriteDocumentStart(document, index, total);

        logger.LogInformation(
            "Extracting document {DocumentIndex}/{DocumentCount} for planning application {ApplicationReference}: {DocumentName} ({DocumentType})",
            index,
            total,
            application.ApplicationReference ?? application.SourceKey ?? applicationUrl,
            document.Name,
            document.DocumentType);

        PlanningDocumentTextExtractionResult extraction;
        try
        {
            await documentDownloadThrottle.WaitAsync(cancellationToken);
            try
            {
                extraction = await documentContentService.ExtractAsync(document.Url, browserContext, cancellationToken);
            }
            finally
            {
                documentDownloadThrottle.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            applicationLog.WriteLine($"Document extraction failed for {document.Url}.");
            applicationLog.WriteException(ex);
            throw;
        }

        document.ContentText = extraction.ContentText;
        document.FileName = extraction.FileName;
        document.ContentType = extraction.ContentType;
        document.ParseStatus = extraction.ParseStatus;
        document.ParseError = extraction.ParseError;
        document.ParsedAt = DateTime.UtcNow;
        applicationLog.WriteDocumentExtractionResult(document);

        logger.LogInformation(
            "Document extraction finished with status {ParseStatus} for {DocumentFileName}; extracted {CharacterCount} characters",
            document.ParseStatus,
            document.FileName ?? document.Name,
            document.ContentText?.Length ?? 0);

        return new ScrapedDocumentExtractionResult(index, document);
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
            rows => {
                const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                const normalizeHeading = value => clean(value).toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
                const rowCells = row => Array.from(row.cells || row.querySelectorAll('th, td'));
                const expectedHeadings = ['date published', 'document type', 'description'];
                const headingMatches = (actual, expected) => actual === expected || actual.includes(expected);
                const headerRow = rows.find(row => {
                    const headings = rowCells(row).map(cell => normalizeHeading(cell.textContent));
                    return expectedHeadings.every(heading => headings.some(actual => headingMatches(actual, heading)));
                });

                if (!headerRow) {
                    return JSON.stringify([]);
                }

                const headerCells = rowCells(headerRow);
                const columnIndex = heading => headerCells.findIndex(cell => headingMatches(normalizeHeading(cell.textContent), heading));
                const columns = {
                    publishedDate: columnIndex('date published'),
                    documentType: columnIndex('document type'),
                    description: columnIndex('description')
                };

                if (columns.publishedDate < 0 || columns.documentType < 0 || columns.description < 0) {
                    return JSON.stringify([]);
                }

                return JSON.stringify(rows.slice(rows.indexOf(headerRow) + 1).map(row => {
                    const cells = rowCells(row);
                    const viewLink = row.querySelector('a[title*="View"], a.recaptcha-link[href]');

                    if (!viewLink) {
                        return null;
                    }

                    return {
                        publishedDate: clean(cells[columns.publishedDate]?.textContent),
                        documentType: clean(cells[columns.documentType]?.textContent),
                        description: clean(cells[columns.description]?.textContent),
                        url: viewLink.href || viewLink.getAttribute('href') || ''
                    };
                }).filter(Boolean));
            }
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

    private static string DescribeSearchResult(SearchResultDto result)
    {
        var sourceKey = ExtractQueryValue(result.Url, "keyVal");
        var reference = ExtractReferenceFromMeta(result.Meta);
        var label = FirstNonEmpty(reference, result.Title, sourceKey, result.Url) ?? "unknown application";
        var parts = new List<string> { label };

        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            parts.Add($"sourceKey={sourceKey}");
        }

        if (!string.IsNullOrWhiteSpace(result.Address))
        {
            parts.Add($"address={NormalizeText(result.Address)}");
        }

        if (!string.IsNullOrWhiteSpace(result.Url))
        {
            parts.Add($"url={result.Url}");
        }

        return string.Join(" | ", parts);
    }

    private static string DescribeScrapedApplication(ScrapedApplication application)
    {
        var label = FirstNonEmpty(application.ApplicationReference, application.Title, application.SourceKey)
            ?? "unknown application";
        var parts = new List<string> { label };

        if (!string.IsNullOrWhiteSpace(application.SourceKey))
        {
            parts.Add($"sourceKey={application.SourceKey}");
        }

        if (!string.IsNullOrWhiteSpace(application.Status))
        {
            parts.Add($"status={application.Status}");
        }

        if (!string.IsNullOrWhiteSpace(application.Address))
        {
            parts.Add($"address={application.Address}");
        }

        return string.Join(" | ", parts);
    }

    private static string FormatUtc(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static bool IsRestartableBrowserFailure(Exception exception)
    {
        return PlaywrightBrowserFailure.IsRestartable(exception);
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

        var match = Regex.Match(
            meta,
            @"(?:Ref\.?\s*No|Application\.?\s*No|Application\s*No|Reference)\s*:\s*(?<reference>.*?)(?:\s*\||$)",
            RegexOptions.IgnoreCase);
        return match.Success ? NormalizeText(match.Groups["reference"].Value) : null;
    }

    private static string? ExtractQueryValue(string url, string key)
    {
        var decodedUrl = WebUtility.HtmlDecode(url);
        var queryStart = decodedUrl.IndexOf('?', StringComparison.Ordinal);
        var query = queryStart >= 0 ? decodedUrl[(queryStart + 1)..] : decodedUrl;
        var fragmentStart = query.IndexOf('#', StringComparison.Ordinal);
        if (fragmentStart >= 0)
        {
            query = query[..fragmentStart];
        }

        foreach (var parameter in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = parameter.IndexOf('=', StringComparison.Ordinal);
            var parameterName = separator >= 0 ? parameter[..separator] : parameter;
            if (!parameterName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameterValue = separator >= 0 ? parameter[(separator + 1)..] : string.Empty;
            return NormalizeText(WebUtility.UrlDecode(parameterValue));
        }

        return null;
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

    private static int NormalizeConcurrency(int configuredValue)
    {
        return Math.Max(1, configuredValue);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        value = NormalizeText(value);
        return value is null || value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed class BrowserSession(ILogger logger) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private int _generation;

        public async Task<BrowserSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            await _restartLock.WaitAsync(cancellationToken);
            try
            {
                var browser = _browser
                    ?? throw new InvalidOperationException("The Playwright browser session has not been started.");

                return new BrowserSnapshot(browser, _generation);
            }
            finally
            {
                _restartLock.Release();
            }
        }

        public async Task StartAsync()
        {
            if (_browser is not null)
            {
                throw new InvalidOperationException("The Playwright browser session has already been started.");
            }

            _playwright = await Playwright.CreateAsync();

            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });
                _generation++;
                logger.LogDebug("Launched headless Chromium for weekly planning import scrape");
            }
            catch
            {
                _playwright.Dispose();
                _playwright = null;
                throw;
            }
        }

        public async Task<bool> RestartAsync(
            TimeSpan restartDelay,
            CancellationToken cancellationToken,
            int? expectedGeneration = null)
        {
            await _restartLock.WaitAsync(cancellationToken);
            try
            {
                if (expectedGeneration is { } generation && generation != _generation)
                {
                    logger.LogDebug(
                        "Skipping Playwright/Chromium restart because another worker already restarted the browser session");
                    return false;
                }

                await StopAsync();

                if (restartDelay > TimeSpan.Zero)
                {
                    await Task.Delay(restartDelay, cancellationToken);
                }

                await StartAsync();
                return true;
            }
            finally
            {
                _restartLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _restartLock.WaitAsync();
            try
            {
                await StopAsync();
            }
            finally
            {
                _restartLock.Release();
            }
        }

        private async Task StopAsync()
        {
            var browser = _browser;
            _browser = null;

            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Ignoring failure while closing Playwright browser during restart");
                }

                try
                {
                    await browser.DisposeAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Ignoring failure while disposing Playwright browser during restart");
                }
            }

            _playwright?.Dispose();
            _playwright = null;
        }
    }

    private sealed class ScraperFileLogger
    {
        private const string DefaultLogDirectory = "Services/ScraperLogs";
        private readonly ILogger _logger;
        private readonly object _writeLock = new();
        private readonly string? _runSummaryPath;

        private ScraperFileLogger(string? rootDirectory, DateTime runStartedAtUtc, ILogger logger)
        {
            RootDirectory = rootDirectory ?? string.Empty;
            RunStamp = runStartedAtUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _logger = logger;
            _runSummaryPath = rootDirectory is null
                ? null
                : Path.Combine(rootDirectory, $"weekly-summary-{RunStamp}.txt");
        }

        public bool Enabled => !string.IsNullOrWhiteSpace(RootDirectory);

        public string RootDirectory { get; }

        public string RunStamp { get; }

        public static ScraperFileLogger Create(
            string contentRootPath,
            string? configuredDirectory,
            DateTime runStartedAtUtc,
            ILogger logger)
        {
            var logDirectory = FirstNonEmpty(configuredDirectory, DefaultLogDirectory)!;
            var rootDirectory = Path.IsPathRooted(logDirectory)
                ? logDirectory
                : Path.GetFullPath(Path.Combine(contentRootPath, logDirectory));

            try
            {
                Directory.CreateDirectory(rootDirectory);
                return new ScraperFileLogger(rootDirectory, runStartedAtUtc, logger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Scraper text logging is disabled because log directory {ScraperLogDirectory} could not be prepared",
                    rootDirectory);

                return new ScraperFileLogger(null, runStartedAtUtc, logger);
            }
        }

        public void WriteRunSummary(string message)
        {
            AppendLine(_runSummaryPath, message);
        }

        public AuthorityFileLog CreateAuthorityLog(PlanningAuthority authority)
        {
            if (!Enabled)
            {
                return AuthorityFileLog.Disabled(this);
            }

            var authorityDirectory = Path.Combine(
                RootDirectory,
                SafeFileName(authority.Name, authority.Id.ToString("N")));

            try
            {
                Directory.CreateDirectory(authorityDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not create scraper log folder {ScraperAuthorityLogDirectory} for {PlanningAuthorityName}",
                    authorityDirectory,
                    authority.Name);

                return AuthorityFileLog.Disabled(this);
            }

            return new AuthorityFileLog(this, authorityDirectory, Path.Combine(authorityDirectory, "summary.txt"));
        }

        public ApplicationFileLog CreateApplicationLog(
            AuthorityFileLog authorityLog,
            SearchResultDto result,
            int index,
            int total)
        {
            if (!authorityLog.Enabled)
            {
                return ApplicationFileLog.Disabled(this);
            }

            var label = FirstNonEmpty(
                ExtractReferenceFromMeta(result.Meta),
                ExtractQueryValue(result.Url, "keyVal"),
                result.Title,
                $"application-{index:000}")!;
            var fileName = $"{RunStamp}-{index:000}-of-{total:000}-{SafeFileName(label, $"application-{index:000}")}.txt";
            var filePath = GetUniquePath(Path.Combine(authorityLog.DirectoryPath, fileName));
            AppendLine(filePath, $"Detail log created for application {index}/{total}.");

            return new ApplicationFileLog(this, filePath);
        }

        public void AppendLine(string? path, string message)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var timestamp = FormatUtc(DateTime.UtcNow);
                var builder = new StringBuilder();
                foreach (var line in SplitLines(message))
                {
                    builder
                        .Append('[')
                        .Append(timestamp)
                        .Append("] ")
                        .AppendLine(line);
                }

                lock (_writeLock)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not write scraper text log {ScraperLogPath}", path);
            }
        }

        private static IEnumerable<string> SplitLines(string message)
        {
            var normalized = message
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var lines = normalized.Split('\n');
            return lines.Length == 0 ? [""] : lines;
        }

        private static string GetUniquePath(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);

            for (var counter = 2; counter < 1000; counter++)
            {
                var candidate = Path.Combine(directory, $"{name}-{counter}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
        }

        private static string SafeFileName(string? value, string fallback)
        {
            var invalidCharacters = Path.GetInvalidFileNameChars()
                .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
                .ToHashSet();
            var normalized = FirstNonEmpty(value, fallback) ?? fallback;
            var builder = new StringBuilder(normalized.Length);

            foreach (var character in normalized)
            {
                builder.Append(invalidCharacters.Contains(character) || char.IsControl(character)
                    ? '-'
                    : character);
            }

            var safe = WhitespaceRegex.Replace(builder.ToString(), " ").Trim(' ', '.', '-');
            if (string.IsNullOrWhiteSpace(safe))
            {
                safe = fallback;
            }

            return safe.Length <= 120 ? safe : safe[..120].Trim(' ', '.', '-');
        }
    }

    private sealed class AuthorityFileLog
    {
        private readonly ScraperFileLogger _owner;
        private readonly string? _summaryPath;

        public AuthorityFileLog(ScraperFileLogger owner, string directoryPath, string summaryPath)
        {
            _owner = owner;
            DirectoryPath = directoryPath;
            _summaryPath = summaryPath;
        }

        private AuthorityFileLog(ScraperFileLogger owner)
        {
            _owner = owner;
            DirectoryPath = string.Empty;
        }

        public bool Enabled => !string.IsNullOrWhiteSpace(_summaryPath);

        public string DirectoryPath { get; }

        public static AuthorityFileLog Disabled(ScraperFileLogger owner)
        {
            return new AuthorityFileLog(owner);
        }

        public void StartRun(PlanningAuthority authority, DateTime startedAtUtc)
        {
            WriteSummary(string.Empty);
            WriteSummary("============================================================");
            WriteSummary($"Run {_owner.RunStamp} started at {FormatUtc(startedAtUtc)}.");
            WriteSummary($"Authority: {authority.Name}");
            WriteSummary($"AuthorityId: {authority.Id}");
            WriteSummary($"Website: {authority.Website}");
            WriteSummary($"Authority log folder: {DirectoryPath}");
        }

        public void WriteSummary(string message)
        {
            _owner.AppendLine(_summaryPath, message);
        }

        public void WriteExceptionSummary(Exception exception)
        {
            WriteSummary("Exception details:");
            _owner.AppendLine(_summaryPath, exception.ToString());
        }

        public ApplicationFileLog CreateApplicationLog(SearchResultDto result, int index, int total)
        {
            return _owner.CreateApplicationLog(this, result, index, total);
        }
    }

    private sealed class ApplicationFileLog
    {
        private readonly ScraperFileLogger _owner;
        private readonly string? _filePath;

        public ApplicationFileLog(ScraperFileLogger owner, string filePath)
        {
            _owner = owner;
            _filePath = filePath;
            FileName = Path.GetFileName(filePath);
        }

        private ApplicationFileLog(ScraperFileLogger owner)
        {
            _owner = owner;
            FileName = "(file logging disabled)";
        }

        public string FileName { get; }

        public static ApplicationFileLog Disabled(ScraperFileLogger owner)
        {
            return new ApplicationFileLog(owner);
        }

        public void WriteSection(string title)
        {
            WriteLine(string.Empty);
            WriteLine($"--- {title} ---");
        }

        public void WriteLine(string message)
        {
            _owner.AppendLine(_filePath, message);
        }

        public void WriteKeyValue(string key, object? value)
        {
            WriteLine($"{key}: {RenderValue(value)}");
        }

        public void WriteDictionary(string title, IReadOnlyDictionary<string, string> values)
        {
            WriteSection(title);
            if (values.Count == 0)
            {
                WriteLine("(no values found)");
                return;
            }

            foreach (var value in values)
            {
                WriteKeyValue(value.Key, value.Value);
            }
        }

        public void WriteContacts(IReadOnlyList<ContactSectionDto> contacts)
        {
            WriteSection("Extracted Contact Sections");
            if (contacts.Count == 0)
            {
                WriteLine("(no contact sections found)");
                return;
            }

            for (var index = 0; index < contacts.Count; index++)
            {
                var contact = contacts[index];
                WriteLine($"Contact section {index + 1}/{contacts.Count}: {contact.Heading}");
                WriteKeyValue("Name", contact.Name);

                if (contact.Rows.Count == 0)
                {
                    WriteLine("Rows: (none)");
                    continue;
                }

                foreach (var row in contact.Rows)
                {
                    WriteKeyValue(row.Key, row.Value);
                }
            }
        }

        public void WriteApplicationFields(ScrapedApplication application)
        {
            WriteSection("Application Fields");
            WriteKeyValue("Title", application.Title);
            WriteKeyValue("Description", application.Description);
            WriteKeyValue("Address", application.Address);
            WriteKeyValue("Application reference", application.ApplicationReference);
            WriteKeyValue("Source key", application.SourceKey);
            WriteKeyValue("Source URL", application.SourceUrl);
            WriteKeyValue("Status", application.Status);
            WriteKeyValue("Received date", application.ReceivedDate);
            WriteKeyValue("Validated date", application.ValidatedDate);
            WriteKeyValue("Applicant name", application.ApplicantName);
            WriteKeyValue("Applicant email", application.ApplicantEmail);
            WriteKeyValue("Applicant phone", application.ApplicantPhone);
            WriteKeyValue("Agent name", application.AgentName);
            WriteKeyValue("Agent email", application.AgentEmail);
            WriteKeyValue("Agent phone", application.AgentPhone);
            WriteKeyValue("Company name", application.CompanyName);
        }

        public void WriteDocumentStart(ScrapedDocument document, int index, int total)
        {
            WriteSection($"Document {index}/{total}");
            WriteKeyValue("Name", document.Name);
            WriteKeyValue("Document type", document.DocumentType);
            WriteKeyValue("Published date", document.PublishedDate);
            WriteKeyValue("URL", document.Url);
        }

        public void WriteDocumentExtractionResult(ScrapedDocument document)
        {
            WriteKeyValue("Parse status", document.ParseStatus);
            WriteKeyValue("Parse error", document.ParseError);
            WriteKeyValue("File name", document.FileName);
            WriteKeyValue("Content type", document.ContentType);
            WriteKeyValue("Parsed at UTC", document.ParsedAt);
            WriteKeyValue("Extracted character count", document.ContentText?.Length ?? 0);
        }

        public void WriteException(Exception exception)
        {
            WriteSection("Exception");
            WriteLine(exception.ToString());
        }

        private static string RenderValue(object? value)
        {
            return value switch
            {
                null => "(null)",
                DateTime dateTime => FormatUtc(dateTime),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                _ => NormalizeText(Convert.ToString(value, CultureInfo.InvariantCulture)) ?? "(empty)"
            };
        }
    }

    private sealed record BrowserSnapshot(IBrowser Browser, int Generation);

    private sealed record SaveResult(int ApplicationsSaved, int DocumentsSaved);

    private sealed record QueuedApplicationWorkItem(
        QueuedApplicationLink Link,
        int QueueIndex,
        int QueueCount);

    private sealed record ScrapedDocumentExtractionResult(int Index, ScrapedDocument Document);

    private sealed record AuthorityApplicationLinkResult(
        IReadOnlyList<SearchResultDto> ResultLinks,
        bool Skipped,
        bool WeekUnavailable,
        WeeklyListSelection? SelectedWeek);

    private sealed record QueuedApplicationLink(
        PlanningAuthority Authority,
        AuthorityFileLog AuthorityLog,
        Uri BaseUri,
        SearchResultDto ResultLink,
        WeeklyListSelection? SelectedWeek,
        int WeekNumber,
        int AuthorityLinkIndex,
        int AuthorityLinkCount);

    private sealed class AuthorityWeeklyScrapeStats(
        PlanningAuthority authority,
        AuthorityFileLog authorityLog,
        string selectedWeekDescription,
        int queuedApplications)
    {
        private readonly object _sync = new();

        public PlanningAuthority Authority { get; } = authority;

        public AuthorityFileLog AuthorityLog { get; } = authorityLog;

        public string SelectedWeekDescription { get; } = selectedWeekDescription;

        public int QueuedApplications { get; } = queuedApplications;

        public int ApplicationsScraped { get; private set; }

        public int ApplicationsSaved { get; private set; }

        public int DocumentsScraped { get; private set; }

        public int DocumentsSaved { get; private set; }

        public void AddScrapeResult(int documentCount)
        {
            lock (_sync)
            {
                ApplicationsScraped++;
                DocumentsScraped += documentCount;
            }
        }

        public void AddSaveResult(SaveResult saveResult)
        {
            lock (_sync)
            {
                ApplicationsSaved += saveResult.ApplicationsSaved;
                DocumentsSaved += saveResult.DocumentsSaved;
            }
        }
    }

    private sealed record PreviouslyScrapedFilterResult(int SkippedLinkCount, IReadOnlyList<string> SkippedApplications)
    {
        public static PreviouslyScrapedFilterResult Empty { get; } = new(0, Array.Empty<string>());
    }

    private sealed record WeeklyListSelectionResult(
        bool WeekSelectFound,
        int OptionCount,
        WeeklyListSelection? SelectedWeek);

    private sealed record WeeklyListSelection(
        int DropdownIndex,
        string Value,
        string Label,
        DateTime? WeekStart);

    private sealed class SelectedWeekDto
    {
        public bool Found { get; set; }

        public int OptionCount { get; set; }

        public int TargetIndex { get; set; }

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
