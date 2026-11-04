using JNotes.Core;
using JNotes.Data;

namespace JNotes.Web.Maintenance;

/// <summary>
/// Nightly housekeeping: database backup, then emptying Trash items and history versions older than 30 days.
/// Runs at JNotes:BackupHour (local time, default 3am), and soon after startup if the last backup is over a day old
/// (the Pi may have been off at 3am).
/// </summary>
public sealed class MaintenanceService(BackupService backups, ITrash trash, INoteHistory history, TimeProvider clock,
    IConfiguration config, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hour = Math.Clamp(config.GetValue("JNotes:BackupHour", 3), 0, 23);
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
            var status = backups.GetStatus();
            if (status.LastBackup is null || clock.GetUtcNow() - status.LastBackup > TimeSpan.FromHours(24))
                await RunAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(UntilNext(hour), clock, stoppingToken);
                await RunAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private TimeSpan UntilNext(int hour)
    {
        var now = clock.GetLocalNow();
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var path = await backups.BackupNowAsync(ct);
            log.LogInformation("Backup written to {Path}", path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Nightly backup failed");
        }
        try
        {
            var purged = await trash.PurgeExpiredAsync(ct);
            var pruned = await history.PruneAsync(ct);
            if (purged + pruned > 0) log.LogInformation("Cleanup: {Purged} trash item(s) and {Pruned} old version(s) removed", purged, pruned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Nightly cleanup failed");
        }
    }
}
