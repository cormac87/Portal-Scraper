using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PortalScraper.Data;

namespace PortalScraper.Services.Companies;

internal static class CompanyQuery
{
    public static IQueryable<Company> ApplyFilters(
        IQueryable<Company> query,
        CompanySearchFilters filters)
    {
        var companyName = filters.CompanyName?.Trim();
        if (!string.IsNullOrWhiteSpace(companyName))
        {
            query = query.Where(company =>
                company.CompanyName != null
                && company.CompanyName.Contains(companyName));
        }

        var sicCode = filters.SicCode?.Trim();
        if (!string.IsNullOrWhiteSpace(sicCode))
        {
            var sicPattern = $"{sicCode} -%";
            query = query.Where(company =>
                company.SicCodeSicText1 == sicCode
                || company.SicCodeSicText2 == sicCode
                || company.SicCodeSicText3 == sicCode
                || company.SicCodeSicText4 == sicCode
                || (company.SicCodeSicText1 != null && EF.Functions.Like(company.SicCodeSicText1, sicPattern))
                || (company.SicCodeSicText2 != null && EF.Functions.Like(company.SicCodeSicText2, sicPattern))
                || (company.SicCodeSicText3 != null && EF.Functions.Like(company.SicCodeSicText3, sicPattern))
                || (company.SicCodeSicText4 != null && EF.Functions.Like(company.SicCodeSicText4, sicPattern)));
        }

        if (filters.Location is not null)
        {
            var origin = CreatePoint(filters.Location);
            var radiusMeters = filters.Location.RadiusKm * 1000d;
            query = query.Where(company =>
                company.Location != null
                && company.Location.Distance(origin) <= radiusMeters);
        }

        return query;
    }

    public static IOrderedQueryable<Company> ApplyDefaultSort(
        IQueryable<Company> query,
        CompanySearchFilters? filters = null)
    {
        if (filters?.Location is not null)
        {
            var origin = CreatePoint(filters.Location);
            return query
                .OrderBy(company => company.Location!.Distance(origin))
                .ThenBy(company => company.CompanyName)
                .ThenBy(company => company.CompanyNumber)
                .ThenBy(company => company.Id);
        }

        return query
            .OrderBy(company => company.CompanyName)
            .ThenBy(company => company.CompanyNumber)
            .ThenBy(company => company.Id);
    }

    public static IOrderedQueryable<Company> ApplyPageSort(
        IQueryable<Company> query,
        CompanySearchFilters? filters = null)
    {
        if (filters?.Location is not null)
        {
            var origin = CreatePoint(filters.Location);
            return query
                .OrderBy(company => company.Location!.Distance(origin))
                .ThenBy(company => company.Id);
        }

        return query.OrderBy(company => company.Id);
    }

    public static Point CreatePoint(CompanyLocationSearch location)
    {
        return new Point(location.Longitude, location.Latitude)
        {
            SRID = 4326
        };
    }

    public static CompanySicCodeOption? ParseSicCodeOption(string? sicText)
    {
        if (string.IsNullOrWhiteSpace(sicText))
        {
            return null;
        }

        var text = sicText.Trim();
        var separatorIndex = text.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            return new CompanySicCodeOption(
                text[..separatorIndex].Trim(),
                text[(separatorIndex + 3)..].Trim());
        }

        var firstSpaceIndex = text.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpaceIndex > 0)
        {
            return new CompanySicCodeOption(
                text[..firstSpaceIndex].Trim(),
                text[(firstSpaceIndex + 1)..].Trim());
        }

        return new CompanySicCodeOption(text, string.Empty);
    }

    public static IEnumerable<CompanySicCodeOption> NormalizeSicCodeOptions(IEnumerable<string?> sicTexts)
    {
        return sicTexts
            .Select(ParseSicCodeOption)
            .OfType<CompanySicCodeOption>()
            .Where(option => !string.IsNullOrWhiteSpace(option.Code))
            .GroupBy(option => option.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(option => option.Description.Length)
                .ThenBy(option => option.Description, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(option => TryParseCodeNumber(option.Code, out var number) ? number : int.MaxValue)
            .ThenBy(option => option.Code, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseCodeNumber(string code, out int number)
    {
        return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }
}
