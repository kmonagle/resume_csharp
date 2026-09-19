// Why this file exists: Entity Framework Core's description of the two tables this
// service reads and writes. They are a MIRROR of the schema, not its source: the
// tables are defined and migrated by Drizzle in the Next.js repo, and this service
// never creates or alters them (so there are no EF migrations here). If the schema
// changes there, these classes must follow.
//
// JS/TS vs C#: this is the counterpart of src/server/db/schema.ts (Drizzle) in the
// Next.js repo, and of Python's app/models.py. A DbContext is EF Core's unit of work:
// one instance holds a connection, tracks the objects you load, and writes changes
// when you call SaveChangesAsync. It is meant to be short-lived (one per request).
using Microsoft.EntityFrameworkCore;

namespace LinkApi.Data;

// JS/TS vs C#: these are plain classes whose properties ARE the columns (the naming
// convention turns `ShortCode` into `short_code`). `string` is NOT NULL and `string?`
// is nullable, because nullable reference types decide nullability, just as
// `Mapped[str | None]` does in the Python version. `required` makes the compiler
// insist that callers set the property when creating one.
public sealed class LinkEntity
{
    public required string Id { get; set; }
    public required string ShortCode { get; set; }
    public required string TargetUrl { get; set; }
    public string? Title { get; set; }
    public required string OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxClicks { get; set; }
    public int ClickCount { get; set; }
    public bool IsActive { get; set; }
}

public sealed class ClickEventEntity
{
    public required string Id { get; set; }
    public required string LinkId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
}

// JS/TS vs C#: a PRIMARY CONSTRUCTOR on a class (`(DbContextOptions<...> options)`)
// is shorthand for a constructor plus a field, and the base-class call is written
// `: DbContext(options)`. The options (connection string, provider) are handed in by
// dependency injection; see Program.cs.
public sealed class LinksDbContext(DbContextOptions<LinksDbContext> options) : DbContext(options)
{
    public DbSet<LinkEntity> Links => Set<LinkEntity>();

    public DbSet<ClickEventEntity> ClickEvents => Set<ClickEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // JS/TS vs C#: "fluent" configuration: chained calls describing the mapping.
        // Column names come from the snake_case naming convention; only what the
        // convention can't infer is spelled out.
        modelBuilder.Entity<LinkEntity>(link =>
        {
            link.ToTable("links");
            link.HasKey(l => l.Id);
            link.HasIndex(l => l.ShortCode).IsUnique();

            // These columns have DATABASE defaults. Telling EF so means it omits them
            // from the INSERT when the value is the type's default, and reads the value
            // the database chose back afterwards. (created_at gets now(), and so on.)
            link.Property(l => l.CreatedAt).HasDefaultValueSql("now()");
            link.Property(l => l.UpdatedAt).HasDefaultValueSql("now()");
            link.Property(l => l.ClickCount).HasDefaultValueSql("0");
            link.Property(l => l.IsActive).HasDefaultValueSql("true");
        });

        modelBuilder.Entity<ClickEventEntity>(click =>
        {
            click.ToTable("click_events");
            click.HasKey(c => c.Id);
            click.Property(c => c.OccurredAt).HasDefaultValueSql("now()");
        });
    }
}
