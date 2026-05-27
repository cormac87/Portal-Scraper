using System.ComponentModel.DataAnnotations;

namespace PortalScraper.Data;

public sealed class PlanningAuthority
{
    public Guid Id { get; set; }

    [Required]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Website { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public ICollection<PlanningApplication> PlanningApplications { get; set; } = [];
}
