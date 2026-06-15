namespace PortalScraper.Services.Planning;

public static class PlanningPagination
{
    public static int GetPageSkip(int page, int pageSize)
    {
        return (Math.Max(1, page) - 1) * pageSize;
    }

    public static int GetTotalPages(int totalCount, int pageSize)
    {
        return Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    public static int ClampPage(int page, int totalCount, int pageSize)
    {
        return Math.Clamp(page, 1, GetTotalPages(totalCount, pageSize));
    }

    public static string GetSummary(int totalCount, int currentPage, int pageSize, string itemName)
    {
        if (totalCount == 0)
        {
            return $"0 {itemName}(s)";
        }

        var start = GetPageSkip(currentPage, pageSize) + 1;
        var end = Math.Min(totalCount, currentPage * pageSize);

        return $"Showing {start}-{end} of {totalCount} {itemName}(s)";
    }

    public static string GetSummary(
        int? totalCount,
        int currentPage,
        int pageSize,
        int loadedCount,
        bool hasNextPage,
        string itemName)
    {
        if (totalCount.HasValue)
        {
            return GetSummary(totalCount.Value, currentPage, pageSize, itemName);
        }

        if (loadedCount == 0)
        {
            return $"0 {itemName}(s)";
        }

        var start = GetPageSkip(currentPage, pageSize) + 1;
        var end = start + loadedCount - 1;
        var suffix = hasNextPage ? " and more" : string.Empty;

        return $"Showing {start}-{end}{suffix} {itemName}(s)";
    }
}
