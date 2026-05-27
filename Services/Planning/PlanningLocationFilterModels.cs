namespace PortalScraper.Services.Planning;

public sealed class PlanningLocationFilterInput
{
    public string Postcode { get; set; } = string.Empty;

    public double RadiusKm { get; set; } = 10;

    public bool HasResolved { get; set; }

    public List<PlanningAuthorityLocationSelection> Authorities { get; set; } = [];

    public IReadOnlyList<Guid> SelectedAuthorityIds => Authorities
        .Where(authority => authority.IsSelected)
        .Select(authority => authority.Id)
        .ToList();
}

public sealed class PlanningDateRangeFilterInput
{
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public bool HasActive => StartDate.HasValue || EndDate.HasValue;

    public bool HasInvalidRange => StartDate.HasValue
        && EndDate.HasValue
        && StartDate.Value.Date > EndDate.Value.Date;
}

public sealed class PlanningAuthorityLocationSelection
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double DistanceKm { get; set; }

    public bool IsSelected { get; set; } = true;
}
