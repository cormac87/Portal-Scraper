using System.ComponentModel.DataAnnotations;

namespace PortalScraper.Data;

public sealed class PlanningDocument
{
    public Guid Id { get; set; }

    [Required]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string DocumentType { get; set; } = string.Empty;

    [Required]
    [StringLength(500)]
    public string Url { get; set; } = string.Empty;

    public DateTime? PublishedDate { get; set; }

    public string? ContentText { get; set; }

    [StringLength(255)]
    public string? FileName { get; set; }

    [StringLength(255)]
    public string? ContentType { get; set; }

    [StringLength(50)]
    public string? ParseStatus { get; set; }

    [StringLength(1000)]
    public string? ParseError { get; set; }

    public DateTime? ParsedAt { get; set; }

    public Guid PlanningApplicationId { get; set; }

    public PlanningApplication PlanningApplication { get; set; } = null!;
}
