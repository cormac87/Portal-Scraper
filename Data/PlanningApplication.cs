using System.ComponentModel.DataAnnotations;

namespace PortalScraper.Data;

public sealed class PlanningApplication
{
    public Guid Id { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public DateTime ScrapedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ReceivedDate { get; set; }

    public DateTime? ValidatedDate { get; set; }

    [StringLength(100)]
    public string? Status { get; set; }

    [StringLength(255)]
    public string? ApplicantEmail { get; set; }

    [StringLength(50)]
    public string? ApplicantPhone { get; set; }

    [StringLength(255)]
    public string? ApplicantName { get; set; }

    [StringLength(255)]
    public string? AgentEmail { get; set; }

    [StringLength(50)]
    public string? AgentPhone { get; set; }

    [StringLength(255)]
    public string? AgentName { get; set; }

    [StringLength(255)]
    public string? CompanyName { get; set; }

    [StringLength(500)]
    public string? Address { get; set; }

    public string? Description { get; set; }

    [StringLength(100)]
    public string? ApplicationReference { get; set; }

    [StringLength(100)]
    public string? SourceKey { get; set; }

    [StringLength(500)]
    public string? SourceUrl { get; set; }

    public Guid PlanningAuthorityId { get; set; }

    public PlanningAuthority PlanningAuthority { get; set; } = null!;

    public ICollection<PlanningDocument> PlanningDocuments { get; set; } = [];
}
