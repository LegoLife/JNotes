using System.Globalization;
using JNotes.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

public sealed record BackupStatus(DateTimeOffset? LastBackup, int LocalCount, long LastSizeBytes, DateTimeOffset? LastDriveSync)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);
    public bool IsStale(DateTimeOffset now) => LastBackup is null || now - LastBackup > StaleAfter;
}

/// <summary>
/// Database snapshots in &lt;data&gt;/backups (jnotes-yyyyMMdd-HHmmss.db) via SQLite's VACUUM INTO, which is
/// safe while the app is running. Images aren't copied here: they never change, so the rclone sidecar
/// copies /data/attachments to Google Drive directly. The sidecar writes .last-drive-sync after each run.
/// </summary>
public sealed class BackupService(IDbContextFactory<JNotesDbContext> factory, TimeProvider clock, string backupDirectory)
{
    public const int KeepLocal = 14;
    public const string DriveSyncMarker = ".last-drive-sync";
    private const string Prefix = "jnotes-";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Directory => backupDirectory;

    public async Task<string> BackupNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(backupDirectory);
            var path = Path.Combine(backupDirectory, $"{Prefix}{clock.GetUtcNow():yyyyMMdd-HHmmss}.db");
            var temp = path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);

            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                // VACUUM INTO can't take a parameter; the path is ours, but escape quotes anyway.
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{temp.Replace("'", "''")}'", ct);
            }
            SqliteConnection.ClearAllPools(); // release any handle on the new file before renaming it
            File.Move(temp, path, overwrite: true);
            Prune();
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public BackupStatus GetStatus()
    {
        var files = Backups();
        var last = files.FirstOrDefault();
        DateTimeOffset? drive = null;
        var marker = Path.Combine(backupDirectory, DriveSyncMarker);
        if (File.Exists(marker) && DateTimeOffset.TryParse(File.ReadAllText(marker).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            drive = d;
        return new BackupStatus(last is null ? null : TimestampOf(last), files.Count, last?.Length ?? 0, drive);
    }

    private void Prune()
    {
        foreach (var old in Backups().Skip(KeepLocal))
            try { old.Delete(); } catch (IOException) { }
    }

    private List<FileInfo> Backups() =>
        System.IO.Directory.Exists(backupDirectory)
            ? new DirectoryInfo(backupDirectory).GetFiles($"{Prefix}*.db").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : [];

    private static DateTimeOffset TimestampOf(FileInfo f) =>
        DateTimeOffset.TryParseExact(f.Name[Prefix.Length..^3], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t : new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero);
}
