using System.Globalization;
using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public static class PlanningDisplayFormatter
{
    public static string DisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Not available" : value;
    }

    public static string FormatDate(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : "Not available";
    }

    public static string FormatDateTime(DateTime value)
    {
        return value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    public static string ApplicationDescription(PlanningApplication application)
    {
        return DisplayValue(!string.IsNullOrWhiteSpace(application.Description) ? application.Description : application.Title);
    }
}
