using JNotes.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace JNotes.Tests;

public class BackupTests
{
    [Fact]
    public async Task Backup_is_a_complete_openable_copy_of_the_database()
    {
        await using var db = await TestDb.CreateAsync();
        await db.Store.CreateNoteAsync("Dune", null, "the spice must flow");
        var dir = Path.Combine(Path.GetTempPath(), $"jnotes-backups-{Guid.NewGuid():N}");
        var backups = new BackupService(db.Services.GetRequiredService<IDbContextFactory<JNotesDbContext>>(), TimeProvider.System, dir);
        try
        {
            var path = await backups.BackupNowAsync();

            await using (var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
            {
                await conn.OpenAsync();
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Content FROM Notes WHERE Title = 'Dune'";
                Assert.Equal("the spice must flow", await cmd.ExecuteScalarAsync());
                cmd.CommandText = "SELECT count(*) FROM NotesFts WHERE NotesFts MATCH 'spice'"; // search index included
                Assert.Equal(1L, await cmd.ExecuteScalarAsync());
            }
            var status = backups.GetStatus();
            Assert.Equal(1, status.LocalCount);
            Assert.False(status.IsStale(DateTimeOffset.UtcNow));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Keeps_the_newest_fourteen_and_reads_the_drive_marker()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero));
        await using var db = await TestDb.CreateAsync();
        var dir = Path.Combine(Path.GetTempPath(), $"jnotes-backups-{Guid.NewGuid():N}");
        var backups = new BackupService(db.Services.GetRequiredService<IDbContextFactory<JNotesDbContext>>(), clock, dir);
        try
        {
            for (var i = 0; i < 16; i++)
            {
                await backups.BackupNowAsync();
                clock.Advance(TimeSpan.FromDays(1));
            }
            await File.WriteAllTextAsync(Path.Combine(dir, BackupService.DriveSyncMarker), "2026-11-16T04:00:00Z\n");

            var status = backups.GetStatus();
            Assert.Equal(14, status.LocalCount);
            Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero), status.LastBackup);
            Assert.Equal(new DateTimeOffset(2026, 11, 16, 4, 0, 0, TimeSpan.Zero), status.LastDriveSync);
            Assert.False(File.Exists(Path.Combine(dir, "jnotes-20261101-030000.db"))); // oldest two pruned
            Assert.True(status.IsStale(new DateTimeOffset(2026, 11, 17, 16, 0, 0, TimeSpan.Zero))); // 37h later
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
