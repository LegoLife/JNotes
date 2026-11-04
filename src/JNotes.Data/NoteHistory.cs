using JNotes.Core;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

public sealed class NoteHistory(IDbContextFactory<JNotesDbContext> factory, TimeProvider clock, INoteStore store, NoteEvents events) : INoteHistory
{
    public async Task SnapshotAsync(Guid noteId, string reason, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null || note.Content.Length == 0) return;
        db.NoteVersions.Add(new NoteVersion { NoteId = noteId, Title = note.Title, Content = note.Content, Reason = Trim(reason), CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<VersionInfo>> ListAsync(Guid noteId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NoteVersions.AsNoTracking()
            .Where(v => v.NoteId == noteId)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => new VersionInfo(v.Id, v.Title, v.Reason, v.CreatedAt, v.Content.Length))
            .ToListAsync(ct);
    }

    public async Task<NoteVersion?> GetAsync(Guid versionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NoteVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
    }

    public async Task<Note> RestoreAsync(Guid versionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var version = await db.NoteVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct)
                      ?? throw new DomainException("That version no longer exists.");
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == version.NoteId, ct)
                   ?? throw new DomainException("The note is in the Trash or was deleted. Restore it first.");

        var now = clock.GetUtcNow();
        db.NoteVersions.Add(new NoteVersion { NoteId = note.Id, Title = note.Title, Content = note.Content, Reason = "Before restore", CreatedAt = now });
        var titleChanged = note.Title != version.Title;
        note.Title = version.Title;
        note.Content = version.Content;
        note.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        events.Raise(new ReplacedEdit(note.Id, note.Content, note.Title));
        if (titleChanged) store.NotifyTreeChanged();
        return note;
    }

    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = clock.GetUtcNow() - Retention.History;
        return await db.NoteVersions.Where(v => v.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string Trim(string s) => s.Length <= 100 ? s : s[..100];
}
