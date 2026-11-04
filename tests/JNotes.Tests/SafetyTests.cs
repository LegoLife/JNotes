using JNotes.Core;
using Microsoft.Extensions.Time.Testing;

namespace JNotes.Tests;

public class HistoryTests
{
    [Fact]
    public async Task Editing_snapshots_the_previous_state_at_most_every_ten_minutes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
        await using var db = await TestDb.CreateAsync(clock);
        var note = await db.Store.CreateNoteAsync("N", null, "v1");

        await db.Store.SaveContentAsync(note.Id, "v2");   // snapshot of v1
        clock.Advance(TimeSpan.FromMinutes(5));
        await db.Store.SaveContentAsync(note.Id, "v3");   // within 10 min: no snapshot
        clock.Advance(TimeSpan.FromMinutes(6));
        await db.Store.SaveContentAsync(note.Id, "v4");   // snapshot of v3

        var versions = await db.History.ListAsync(note.Id);
        Assert.Equal(["v3", "v1"], await Contents(db, versions));
        Assert.All(versions, v => Assert.Equal("While editing", v.Reason));
    }

    [Fact]
    public async Task Claude_edits_always_snapshot_first_with_a_reason()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Store.CreateNoteAsync("N", null, "## Summary\n\nmine\n");

        await db.Ops.ReplaceSectionAsync("N", "Summary", "Claude's version");

        var v = Assert.Single(await db.History.ListAsync(note.Id));
        Assert.Equal("Before Claude: rewrote “Summary”", v.Reason);
        Assert.Equal("## Summary\n\nmine\n", (await db.History.GetAsync(v.Id))!.Content);
    }

    [Fact]
    public async Task Restore_brings_back_title_and_content_and_can_itself_be_undone()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Store.CreateNoteAsync("Original", null, "old text");
        await db.Store.SaveContentAsync(note.Id, "new text");
        await db.Store.RenameNoteAsync(note.Id, "Renamed");
        NoteEdit? edit = null;
        db.Events.ExternalEdit += e => edit = e;

        var restored = await db.History.RestoreAsync((await db.History.ListAsync(note.Id)).Single().Id);

        Assert.Equal(("Original", "old text"), (restored.Title, restored.Content));
        Assert.Equal("old text", (await db.Store.GetNoteAsync(note.Id))!.Content);
        Assert.Single(await db.Search.SearchAsync("old"));            // index follows the restore
        Assert.IsType<ReplacedEdit>(edit);                              // open editors reload
        var before = (await db.History.ListAsync(note.Id))[0];
        Assert.Equal("Before restore", before.Reason);
        Assert.Equal("new text", (await db.History.GetAsync(before.Id))!.Content);
    }

    [Fact]
    public async Task Old_versions_are_pruned_after_thirty_days()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var db = await TestDb.CreateAsync(clock);
        var note = await db.Store.CreateNoteAsync("N", null, "a");
        await db.History.SnapshotAsync(note.Id, "old");
        clock.Advance(TimeSpan.FromDays(29));
        await db.History.SnapshotAsync(note.Id, "recent");
        clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(1, await db.History.PruneAsync());
        Assert.Equal("recent", Assert.Single(await db.History.ListAsync(note.Id)).Reason);
    }

    private static async Task<string[]> Contents(TestDb db, IEnumerable<VersionInfo> versions)
    {
        var result = new List<string>();
        foreach (var v in versions) result.Add((await db.History.GetAsync(v.Id))!.Content);
        return result.ToArray();
    }
}

public class TrashTests
{
    [Fact]
    public async Task Deleted_items_disappear_everywhere_but_the_trash()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var dune = await db.Store.CreateNoteAsync("Dune", books.Id, "spice");

        await db.Store.DeleteFolderAsync(books.Id);

        Assert.Empty(await db.Store.GetTreeAsync());
        Assert.Null(await db.Store.GetNoteAsync(dune.Id));
        Assert.Empty(await db.Search.SearchAsync("spice"));
        Assert.Empty(await db.Store.GetRecentNotesAsync(10));
        await Assert.ThrowsAsync<DomainException>(() => db.Store.CreateNoteAsync("x", books.Id)); // can't add into a trashed folder
        await Assert.ThrowsAsync<DomainException>(() => db.Ops.ResolveNoteAsync("Dune"));          // Claude can't see it either

        var item = Assert.Single(await db.Trash.ListAsync());
        Assert.Equal(("Books", true, 1, 0, "Top level"), (item.Name, item.IsFolder, item.NoteCount, item.FolderCount, item.OriginalLocation));
    }

    [Fact]
    public async Task Restoring_brings_back_exactly_what_was_deleted_together()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var scifi = await db.Store.CreateFolderAsync("Sci-fi", books.Id);
        var dune = await db.Store.CreateNoteAsync("Dune", scifi.Id, "spice");
        var draft = await db.Store.CreateNoteAsync("Draft", books.Id);
        var draftBatch = await db.Store.DeleteNoteAsync(draft.Id);   // deleted separately, earlier
        var booksBatch = await db.Store.DeleteFolderAsync(books.Id);

        await db.Trash.RestoreAsync(booksBatch);

        var tree = await db.Store.GetTreeAsync();
        Assert.Equal(["Books", "Dune", "Sci-fi"], tree.Select(e => e.Name).Order()); // "Draft" stays in the trash
        Assert.Single(await db.Search.SearchAsync("spice"));
        Assert.Equal(draftBatch, Assert.Single(await db.Trash.ListAsync()).Batch);
    }

    [Fact]
    public async Task Restoring_into_a_folder_that_is_gone_goes_to_the_top_level()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var note = await db.Store.CreateNoteAsync("Dune", books.Id);
        var noteBatch = await db.Store.DeleteNoteAsync(note.Id);
        await db.Store.DeleteFolderAsync(books.Id);

        await db.Trash.RestoreAsync(noteBatch);

        Assert.Null((await db.Store.GetNoteAsync(note.Id))!.FolderId);
    }

    [Fact]
    public async Task Deleting_forever_keeps_separately_trashed_children_restorable()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var keep = await db.Store.CreateNoteAsync("Keep", books.Id, "precious");
        await db.History.SnapshotAsync(keep.Id, "v");
        var gone = await db.Store.CreateNoteAsync("Gone", books.Id, "bye");
        await db.History.SnapshotAsync(gone.Id, "v");
        var keepBatch = await db.Store.DeleteNoteAsync(keep.Id);
        var booksBatch = await db.Store.DeleteFolderAsync(books.Id);

        await db.Trash.DeleteForeverAsync(booksBatch);
        await db.Trash.RestoreAsync(keepBatch);

        Assert.Equal("precious", (await db.Store.GetNoteAsync(keep.Id))!.Content);
        Assert.Empty(await db.History.ListAsync(gone.Id)); // versions of deleted notes go with them
        Assert.Single(await db.History.ListAsync(keep.Id));
    }

    [Fact]
    public async Task Items_older_than_thirty_days_are_purged()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var db = await TestDb.CreateAsync(clock);
        var old = await db.Store.CreateNoteAsync("Old", null);
        await db.Store.DeleteNoteAsync(old.Id);
        clock.Advance(TimeSpan.FromDays(20));
        var recent = await db.Store.CreateNoteAsync("Recent", null);
        await db.Store.DeleteNoteAsync(recent.Id);
        clock.Advance(TimeSpan.FromDays(11));

        Assert.Equal(1, await db.Trash.PurgeExpiredAsync());
        Assert.Equal("Recent", Assert.Single(await db.Trash.ListAsync()).Name);
    }
}
