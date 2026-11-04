using System.Text.Json;
using JNotes.Core;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

/// <summary>
/// Full-text search over the NotesFts FTS5 table (kept in sync with Notes by triggers — see the
/// Step2 migration). Title matches are weighted well above body matches.
/// </summary>
public sealed class NoteSearch(IDbContextFactory<JNotesDbContext> factory) : INoteSearch
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, Guid? folderId = null, int limit = 50, CancellationToken ct = default)
    {
        var fts = Search.BuildFtsQuery(query);
        if (fts is null) return [];

        await using var db = await factory.CreateDbContextAsync(ct);

        // EF stores Guids as upper-case TEXT; folder filter includes all subfolders.
        string? folderJson = null;
        if (folderId is { } fid)
        {
            var folders = await db.Folders.AsNoTracking()
                .Select(f => new TreeEntry(f.Id, f.Name, f.ParentId, true)).ToListAsync(ct);
            var index = new NoteTreeIndex(folders);
            var ids = index.DescendantsOf(fid).Select(d => d.Id).Append(fid);
            folderJson = JsonSerializer.Serialize(ids.Select(i => i.ToString().ToUpperInvariant()));
        }

        var start = Search.HighlightStart.ToString();
        var end = Search.HighlightEnd.ToString();
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT n.Id AS NoteId, n.Title, n.FolderId, n.UpdatedAt AS UpdatedTicks,
                   snippet(NotesFts, 2, {start}, {end}, '…', 20) AS Snippet
            FROM NotesFts
            JOIN Notes n ON n.Id = NotesFts.NoteId
            WHERE NotesFts MATCH {fts}
              AND n.DeletedAt IS NULL
              AND ({folderJson} IS NULL OR n.FolderId IN (SELECT value FROM json_each({folderJson})))
            ORDER BY bm25(NotesFts, 0.0, 10.0, 1.0)
            LIMIT {limit}
            """).ToListAsync(ct);

        return rows.Select(r => new SearchHit(r.NoteId, r.Title, r.FolderId, r.Snippet ?? "",
            new DateTimeOffset(r.UpdatedTicks, TimeSpan.Zero))).ToList();
    }

    private sealed record Row(Guid NoteId, string Title, Guid? FolderId, long UpdatedTicks, string? Snippet);
}
