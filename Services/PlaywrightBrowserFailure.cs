using Microsoft.Playwright;

namespace PortalScraper.Services;

internal static class PlaywrightBrowserFailure
{
    private static readonly string[] RestartableMessages =
    [
        "Target page, context or browser has been closed",
        "Target closed",
        "Browser has been closed",
        "Browser closed",
        "Browser is closed",
        "Browser crashed",
        "Page crashed",
        "Connection closed",
        "Pipe closed",
        "WebSocket closed",
        "WebSocket is not connected",
        "Transport closed",
        "Process exited",
        "ECONNRESET",
        "EPIPE"
    ];

    public static bool IsRestartable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not PlaywrightException)
            {
                continue;
            }

            if (RestartableMessages.Any(message =>
                    current.Message.Contains(message, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
