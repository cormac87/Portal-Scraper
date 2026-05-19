using System.ComponentModel.DataAnnotations;

namespace PortalScraper.Data;

public sealed class TodoItem
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Title { get; set; } = string.Empty;

    public bool IsComplete { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

