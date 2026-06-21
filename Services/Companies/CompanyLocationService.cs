using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PortalScraper.Data;
using PortalScraper.Services.Geocoding;

namespace PortalScraper.Services.Companies;

public sealed class CompanyLocationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPostcodeGeocodingService postcodeGeocodingService) : ICompanyLocationService
{
    private const int BulkPostcodeLookupSize = 100;

    public async Task<CompanyLocationCacheStats> GetCacheStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = (SqlConnection)db.Database.GetDbConnection();

        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        ApplyConfiguredCommandTimeout(db, command);
        command.CommandText = @"
SELECT
    COUNT_BIG(*) AS [TotalCompanies],
    SUM(CASE WHEN [Location] IS NOT NULL THEN CONVERT(BIGINT, 1) ELSE CONVERT(BIGINT, 0) END) AS [CompaniesWithLocation],
    SUM(CASE WHEN [Location] IS NULL AND [NormalizedPostcode] IS NOT NULL AND [NormalizedPostcode] <> N'' THEN CONVERT(BIGINT, 1) ELSE CONVERT(BIGINT, 0) END) AS [CompaniesMissingLocationWithPostcode]
FROM [dbo].[Company];

SELECT COUNT_BIG(*)
FROM
(
    SELECT [NormalizedPostcode]
    FROM [dbo].[Company]
    WHERE [Location] IS NULL
        AND [NormalizedPostcode] IS NOT NULL
        AND [NormalizedPostcode] <> N''
        AND ([LocationLookupStatus] IS NULL OR [LocationLookupStatus] = N'Failed')
    GROUP BY [NormalizedPostcode]
) AS [pending];";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CompanyLocationCacheStats(0, 0, 0, 0);
        }

        var totalCompanies = reader.GetInt64(0);
        var companiesWithLocation = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        var companiesMissingLocationWithPostcode = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);

        var distinctPostcodesPendingLookup = 0;
        if (await reader.NextResultAsync(cancellationToken)
            && await reader.ReadAsync(cancellationToken))
        {
            distinctPostcodesPendingLookup = Convert.ToInt32(Math.Min(reader.GetInt64(0), int.MaxValue));
        }

        return new CompanyLocationCacheStats(
            totalCompanies,
            companiesWithLocation,
            companiesMissingLocationWithPostcode,
            distinctPostcodesPendingLookup);
    }

    public async Task<CompanyLocationRefreshResult> RefreshCompanyLocationsAsync(
        int maxPostcodes,
        CancellationToken cancellationToken = default)
    {
        if (maxPostcodes <= 0)
        {
            throw new InvalidOperationException("Enter at least one postcode to process.");
        }

        var startedAtUtc = DateTime.UtcNow;
        var requestedPostcodes = Math.Max(1, maxPostcodes);
        var items = new List<CompanyLocationRefreshItem>();
        var attemptedPostcodes = 0;
        var updatedPostcodes = 0;
        var notFoundPostcodes = 0;
        var failedPostcodes = 0;
        var updatedCompanies = 0;
        var notFoundCompanies = 0;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);

        var pendingPostcodes = await LoadPendingPostcodesAsync(
            db,
            connection,
            requestedPostcodes,
            cancellationToken);

        foreach (var batch in pendingPostcodes.Chunk(BulkPostcodeLookupSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchUpdates = await BuildBatchUpdatesAsync(batch, cancellationToken);
            var updateResult = await UpdateCompanyLocationsAsync(db, connection, batchUpdates, cancellationToken);

            attemptedPostcodes += batchUpdates.Count;
            updatedPostcodes += batchUpdates.Count(item => item.Status == CompanyLocationLookupStatus.Updated);
            notFoundPostcodes += batchUpdates.Count(item => item.Status == CompanyLocationLookupStatus.NotFound);
            failedPostcodes += batchUpdates.Count(item => item.Status == CompanyLocationLookupStatus.Failed);
            updatedCompanies += updateResult.UpdatedCompanies;
            notFoundCompanies += updateResult.NotFoundCompanies;

            items.AddRange(batchUpdates.Select(item => new CompanyLocationRefreshItem(
                item.Postcode,
                item.CompanyCount,
                item.Status,
                item.Latitude,
                item.Longitude,
                item.Message)));
        }

        var stats = await GetCacheStatsAsync(cancellationToken);
        return new CompanyLocationRefreshResult(
            startedAtUtc,
            DateTime.UtcNow,
            requestedPostcodes,
            attemptedPostcodes,
            updatedPostcodes,
            notFoundPostcodes,
            failedPostcodes,
            updatedCompanies,
            notFoundCompanies,
            items,
            stats);
    }

    private async Task<List<PendingPostcode>> LoadPendingPostcodesAsync(
        ApplicationDbContext db,
        SqlConnection connection,
        int maxPostcodes,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ApplyConfiguredCommandTimeout(db, command);
        command.CommandText = @"
SELECT TOP (@MaxPostcodes)
    [NormalizedPostcode],
    COUNT_BIG(*) AS [CompanyCount]
FROM [dbo].[Company]
WHERE [Location] IS NULL
    AND [NormalizedPostcode] IS NOT NULL
    AND [NormalizedPostcode] <> N''
    AND ([LocationLookupStatus] IS NULL OR [LocationLookupStatus] = N'Failed')
GROUP BY [NormalizedPostcode]
ORDER BY [NormalizedPostcode];";
        command.Parameters.Add(new SqlParameter("@MaxPostcodes", SqlDbType.Int)
        {
            Value = maxPostcodes
        });

        var postcodes = new List<PendingPostcode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            postcodes.Add(new PendingPostcode(
                reader.GetString(0),
                reader.GetInt64(1)));
        }

        return postcodes;
    }

    private async Task<List<CompanyLocationCacheUpdate>> BuildBatchUpdatesAsync(
        IReadOnlyList<PendingPostcode> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await postcodeGeocodingService.GeocodePostcodesAsync(
                batch.Select(item => item.Postcode).ToList(),
                cancellationToken);
            var resultsByPostcode = results
                .Where(result => !string.IsNullOrWhiteSpace(result.Query))
                .GroupBy(result => NormalizePostcode(result.Query), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            return batch
                .Select(item =>
                {
                    var found = resultsByPostcode.TryGetValue(item.Postcode, out var result)
                        && result.Coordinate is not null;

                    return found
                        ? new CompanyLocationCacheUpdate(
                            item.Postcode,
                            item.CompanyCount,
                            CompanyLocationLookupStatus.Updated,
                            result!.Coordinate!.Latitude,
                            result.Coordinate.Longitude,
                            null)
                        : new CompanyLocationCacheUpdate(
                            item.Postcode,
                            item.CompanyCount,
                            CompanyLocationLookupStatus.NotFound,
                            null,
                            null,
                            "Postcode not found.");
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return batch
                .Select(item => new CompanyLocationCacheUpdate(
                    item.Postcode,
                    item.CompanyCount,
                    CompanyLocationLookupStatus.Failed,
                    null,
                    null,
                    ex.Message))
                .ToList();
        }
    }

    private async Task<CompanyLocationDatabaseUpdateResult> UpdateCompanyLocationsAsync(
        ApplicationDbContext db,
        SqlConnection connection,
        IReadOnlyList<CompanyLocationCacheUpdate> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return new CompanyLocationDatabaseUpdateResult(0, 0);
        }

        await ExecuteNonQueryAsync(db, connection, @"
IF OBJECT_ID('tempdb..#CompanyLocationLookup') IS NOT NULL
BEGIN
    DROP TABLE #CompanyLocationLookup;
END;

CREATE TABLE #CompanyLocationLookup
(
    [NormalizedPostcode] NVARCHAR(20) NOT NULL PRIMARY KEY,
    [Latitude] FLOAT NULL,
    [Longitude] FLOAT NULL,
    [Status] NVARCHAR(30) NOT NULL,
    [Message] NVARCHAR(255) NULL
);", cancellationToken);

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            BatchSize = updates.Count,
            DestinationTableName = "#CompanyLocationLookup"
        };
        ApplyConfiguredBulkCopyTimeout(db, bulkCopy);
        bulkCopy.ColumnMappings.Add(nameof(CompanyLocationCacheUpdate.Postcode), "NormalizedPostcode");
        bulkCopy.ColumnMappings.Add(nameof(CompanyLocationCacheUpdate.Latitude), "Latitude");
        bulkCopy.ColumnMappings.Add(nameof(CompanyLocationCacheUpdate.Longitude), "Longitude");
        bulkCopy.ColumnMappings.Add(nameof(CompanyLocationCacheUpdate.Status), "Status");
        bulkCopy.ColumnMappings.Add(nameof(CompanyLocationCacheUpdate.Message), "Message");
        await bulkCopy.WriteToServerAsync(BuildUpdateTable(updates), cancellationToken);

        var updatedCompanies = await ExecuteAffectedRowsAsync(db, connection, @"
UPDATE [company]
SET
    [Latitude] = [lookup].[Latitude],
    [Longitude] = [lookup].[Longitude],
    [Location] = geography::Point([lookup].[Latitude], [lookup].[Longitude], 4326),
    [LocationLookupStatus] = [lookup].[Status],
    [LocationLookupMessage] = NULL,
    [LocationLookupAtUtc] = SYSUTCDATETIME()
FROM [dbo].[Company] AS [company]
INNER JOIN #CompanyLocationLookup AS [lookup]
    ON [company].[NormalizedPostcode] = [lookup].[NormalizedPostcode]
WHERE [lookup].[Status] = N'Updated'
    AND [company].[Location] IS NULL;", cancellationToken);

        var notFoundCompanies = await ExecuteAffectedRowsAsync(db, connection, @"
UPDATE [company]
SET
    [Latitude] = NULL,
    [Longitude] = NULL,
    [Location] = NULL,
    [LocationLookupStatus] = [lookup].[Status],
    [LocationLookupMessage] = [lookup].[Message],
    [LocationLookupAtUtc] = SYSUTCDATETIME()
FROM [dbo].[Company] AS [company]
INNER JOIN #CompanyLocationLookup AS [lookup]
    ON [company].[NormalizedPostcode] = [lookup].[NormalizedPostcode]
WHERE [lookup].[Status] <> N'Updated'
    AND [company].[Location] IS NULL;", cancellationToken);

        return new CompanyLocationDatabaseUpdateResult(updatedCompanies, notFoundCompanies);
    }

    private static DataTable BuildUpdateTable(IReadOnlyList<CompanyLocationCacheUpdate> updates)
    {
        var table = new DataTable
        {
            Locale = CultureInfo.InvariantCulture
        };
        table.Columns.Add(nameof(CompanyLocationCacheUpdate.Postcode), typeof(string));
        table.Columns.Add(nameof(CompanyLocationCacheUpdate.Latitude), typeof(double));
        table.Columns.Add(nameof(CompanyLocationCacheUpdate.Longitude), typeof(double));
        table.Columns.Add(nameof(CompanyLocationCacheUpdate.Status), typeof(string));
        table.Columns.Add(nameof(CompanyLocationCacheUpdate.Message), typeof(string));

        foreach (var update in updates)
        {
            var row = table.NewRow();
            row[nameof(CompanyLocationCacheUpdate.Postcode)] = update.Postcode;
            row[nameof(CompanyLocationCacheUpdate.Latitude)] = update.Latitude ?? (object)DBNull.Value;
            row[nameof(CompanyLocationCacheUpdate.Longitude)] = update.Longitude ?? (object)DBNull.Value;
            row[nameof(CompanyLocationCacheUpdate.Status)] = update.Status;
            row[nameof(CompanyLocationCacheUpdate.Message)] = update.Message ?? (object)DBNull.Value;
            table.Rows.Add(row);
        }

        return table;
    }

    private static async Task ExecuteNonQueryAsync(
        ApplicationDbContext db,
        SqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ApplyConfiguredCommandTimeout(db, command);
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteAffectedRowsAsync(
        ApplicationDbContext db,
        SqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ApplyConfiguredCommandTimeout(db, command);
        command.CommandText = commandText;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ApplyConfiguredCommandTimeout(ApplicationDbContext db, SqlCommand command)
    {
        var commandTimeout = db.Database.GetCommandTimeout();
        if (commandTimeout.HasValue)
        {
            command.CommandTimeout = commandTimeout.Value;
        }
    }

    private static void ApplyConfiguredBulkCopyTimeout(ApplicationDbContext db, SqlBulkCopy bulkCopy)
    {
        var commandTimeout = db.Database.GetCommandTimeout();
        if (commandTimeout.HasValue)
        {
            bulkCopy.BulkCopyTimeout = commandTimeout.Value;
        }
    }

    private static string NormalizePostcode(string postcode)
    {
        return postcode.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static class CompanyLocationLookupStatus
    {
        public const string Updated = "Updated";
        public const string NotFound = "Not found";
        public const string Failed = "Failed";
    }

    private sealed record PendingPostcode(
        string Postcode,
        long CompanyCount);

    private sealed record CompanyLocationCacheUpdate(
        string Postcode,
        long CompanyCount,
        string Status,
        double? Latitude,
        double? Longitude,
        string? Message);

    private sealed record CompanyLocationDatabaseUpdateResult(
        int UpdatedCompanies,
        int NotFoundCompanies);
}
