using Microsoft.EntityFrameworkCore;

namespace PortalScraper.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<PlanningAuthority> PlanningAuthorities => Set<PlanningAuthority>();
    public DbSet<PlanningApplication> PlanningApplications => Set<PlanningApplication>();
    public DbSet<PlanningDocument> PlanningDocuments => Set<PlanningDocument>();
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PlanningAuthority>(entity =>
        {
            entity.ToTable("PlanningAuthority");

            entity.Property(item => item.Id)
                .HasDefaultValueSql("NEWID()");

            entity.Property(item => item.Name)
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(item => item.Website)
                .HasMaxLength(500);

            entity.HasIndex(item => item.Name)
                .HasDatabaseName("IX_PlanningAuthority_Name");
        });

        modelBuilder.Entity<PlanningApplication>(entity =>
        {
            entity.ToTable("PlanningApplication");

            entity.Property(item => item.Id)
                .HasDefaultValueSql("NEWID()");

            entity.Property(item => item.Title)
                .IsRequired();

            entity.Property(item => item.ScrapedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            entity.Property(item => item.ApplicantEmail)
                .HasMaxLength(255);

            entity.Property(item => item.ApplicantPhone)
                .HasMaxLength(50);

            entity.Property(item => item.ApplicantName)
                .HasMaxLength(255);

            entity.Property(item => item.AgentEmail)
                .HasMaxLength(255);

            entity.Property(item => item.AgentPhone)
                .HasMaxLength(50);

            entity.Property(item => item.AgentName)
                .HasMaxLength(255);

            entity.Property(item => item.CompanyName)
                .HasMaxLength(255);

            entity.Property(item => item.Address)
                .HasMaxLength(500);

            entity.Property(item => item.ApplicationReference)
                .HasMaxLength(100);

            entity.Property(item => item.SourceKey)
                .HasMaxLength(100);

            entity.Property(item => item.SourceUrl)
                .HasMaxLength(500);

            entity.Property(item => item.Status)
                .HasMaxLength(100);

            entity.HasIndex(item => new { item.PlanningAuthorityId, item.ApplicationReference })
                .IsUnique()
                .HasDatabaseName("UX_PlanningApplication_Authority_Reference")
                .HasFilter("[ApplicationReference] IS NOT NULL");

            entity.HasIndex(item => new { item.PlanningAuthorityId, item.SourceKey })
                .HasDatabaseName("IX_PlanningApplication_Authority_SourceKey")
                .HasFilter("[SourceKey] IS NOT NULL");

            entity.HasIndex(item => new { item.PlanningAuthorityId, item.ValidatedDate })
                .HasDatabaseName("IX_PlanningApplication_Authority_ValidatedDate")
                .HasFilter("[ValidatedDate] IS NOT NULL");

            entity.HasIndex(item => new { item.PlanningAuthorityId, item.ReceivedDate })
                .HasDatabaseName("IX_PlanningApplication_Authority_ReceivedDate")
                .HasFilter("[ReceivedDate] IS NOT NULL");

            entity.HasIndex(item => new { item.ScrapedAt, item.ApplicationReference, item.Id })
                .HasDatabaseName("IX_PlanningApplication_ScrapedAt_Reference_Id")
                .IsDescending(true, false, false);

            entity.HasOne(item => item.PlanningAuthority)
                .WithMany(authority => authority.PlanningApplications)
                .HasForeignKey(item => item.PlanningAuthorityId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<PlanningDocument>(entity =>
        {
            entity.ToTable("PlanningDocument");

            entity.Property(item => item.Id)
                .HasDefaultValueSql("NEWID()");

            entity.Property(item => item.Name)
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(item => item.DocumentType)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(item => item.Url)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(item => item.FileName)
                .HasMaxLength(255);

            entity.Property(item => item.ContentType)
                .HasMaxLength(255);

            entity.Property(item => item.ParseStatus)
                .HasMaxLength(50);

            entity.Property(item => item.ParseError)
                .HasMaxLength(1000);

            entity.HasIndex(item => new { item.PlanningApplicationId, item.Url })
                .IsUnique();

            entity.HasIndex(item => new { item.PlanningApplicationId, item.PublishedDate })
                .HasDatabaseName("IX_PlanningDocument_Application_PublishedDate")
                .HasFilter("[PublishedDate] IS NOT NULL");

            entity.HasOne(item => item.PlanningApplication)
                .WithMany(application => application.PlanningDocuments)
                .HasForeignKey(item => item.PlanningApplicationId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<TodoItem>(entity =>
        {
            entity.ToTable("TodoItems");

            entity.Property(item => item.Title)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(item => item.IsComplete)
                .HasDefaultValue(false);

            entity.Property(item => item.CreatedAtUtc)
                .HasDefaultValueSql("SYSUTCDATETIME()");
        });
    }
}
