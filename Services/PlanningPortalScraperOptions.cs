namespace PortalScraper.Services;

public sealed class PlanningPortalScraperOptions
{
    public const string SectionName = "PlanningPortalScraper";

    public int DefaultTimeoutMilliseconds { get; set; } = 30_000;

    public int DefaultNavigationTimeoutMilliseconds { get; set; } = 60_000;

    public int BrowserFailureRetryCount { get; set; } = 1;

    public int BrowserRestartDelayMilliseconds { get; set; } = 30_000;

    public string LogDirectory { get; set; } = "Services/ScraperLogs";
}
