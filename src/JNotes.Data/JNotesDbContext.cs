using JNotes.Core;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

public sealed class JNotesDbContext(DbContextOptions<JNotesDbContext> options) : DbContext(options)
{
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Folder>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Names.MaxLength);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ParentId);
        });

        b.Entity<Note>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Names.MaxLength);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.FolderId);
            e.HasIndex(x => x.UpdatedAt);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // SQLite can't ORDER BY DateTimeOffset natively; store as UTC ticks.
        c.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcTicksConverter>();
    }
}

internal sealed class DateTimeOffsetToUtcTicksConverter()
    : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));
