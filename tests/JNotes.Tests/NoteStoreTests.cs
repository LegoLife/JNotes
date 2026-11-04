using JNotes.Core;

namespace JNotes.Tests;

public class NoteStoreTests
{
    [Fact]
    public async Task Notes_and_folders_round_trip_with_markdown_content()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var note = await db.Store.CreateNoteAsync("Dune", books.Id);

        await db.Store.SaveContentAsync(note.Id, "# Chapter 1\n\n- spice\n- **sand**");

        var loaded = await db.Store.GetNoteAsync(note.Id);
        Assert.Equal("# Chapter 1\n\n- spice\n- **sand**", loaded!.Content);
        Assert.Equal(books.Id, loaded.FolderId);
    }

    [Fact]
    public async Task Tree_lists_folders_before_notes_alphabetically()
    {
        await using var db = await TestDb.CreateAsync();
        await db.Store.CreateNoteAsync("b note", null);
        await db.Store.CreateFolderAsync("Zeta", null);
        await db.Store.CreateNoteAsync("A note", null);
        await db.Store.CreateFolderAsync("alpha", null);

        var names = (await db.Store.GetTreeAsync()).Select(e => e.Name);

        Assert.Equal(["alpha", "Zeta", "A note", "b note"], names);
    }

    [Fact]
    public async Task Custom_order_mixes_folders_and_notes_and_new_items_go_last()
    {
        await using var db = await TestDb.CreateAsync();
        var folder = await db.Store.CreateFolderAsync("Folder", null);
        var a = await db.Store.CreateNoteAsync("A", null);
        var b = await db.Store.CreateNoteAsync("B", null);

        await db.Store.ReorderAsync(null, [b.Id, folder.Id, a.Id]);
        await db.Store.CreateNoteAsync("0 new", null);

        Assert.Equal(["B", "Folder", "A", "0 new"], (await db.Store.GetTreeAsync()).Select(e => e.Name));
    }

    [Fact]
    public async Task Reorder_keeps_unlisted_children_after_listed_ones_and_ignores_other_folders()
    {
        await using var db = await TestDb.CreateAsync();
        var inbox = await db.Store.CreateFolderAsync("Inbox", null);
        var a = await db.Store.CreateNoteAsync("A", inbox.Id);
        await db.Store.CreateNoteAsync("B", inbox.Id);
        var c = await db.Store.CreateNoteAsync("C", inbox.Id);
        var outside = await db.Store.CreateNoteAsync("Outside", null);

        await db.Store.ReorderAsync(inbox.Id, [c.Id, outside.Id, a.Id]);

        var tree = await db.Store.GetTreeAsync();
        Assert.Equal(["C", "A", "B"], tree.Where(e => e.ParentId == inbox.Id).Select(e => e.Name));
        Assert.Null(tree.Single(e => e.Id == outside.Id).ParentId);
    }

    [Fact]
    public async Task Moving_into_an_unordered_folder_stays_alphabetical_but_into_an_ordered_one_goes_last()
    {
        await using var db = await TestDb.CreateAsync();
        var plain = await db.Store.CreateFolderAsync("Plain", null);
        var ordered = await db.Store.CreateFolderAsync("Ordered", null);
        await db.Store.CreateNoteAsync("M", plain.Id);
        var y = await db.Store.CreateNoteAsync("Y", ordered.Id);
        var z = await db.Store.CreateNoteAsync("Z", ordered.Id);
        await db.Store.ReorderAsync(ordered.Id, [z.Id, y.Id]);
        var a1 = await db.Store.CreateNoteAsync("A1", null);
        var a2 = await db.Store.CreateNoteAsync("A2", null);

        await db.Store.MoveNoteAsync(a1.Id, plain.Id);
        await db.Store.MoveNoteAsync(a2.Id, ordered.Id);

        var tree = await db.Store.GetTreeAsync();
        Assert.Equal(["A1", "M"], tree.Where(e => e.ParentId == plain.Id).Select(e => e.Name));
        Assert.Equal(["Z", "Y", "A2"], tree.Where(e => e.ParentId == ordered.Id).Select(e => e.Name));
    }

    [Fact]
    public async Task Deleting_a_folder_removes_everything_beneath_it()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var scifi = await db.Store.CreateFolderAsync("Sci-fi", books.Id);
        await db.Store.CreateNoteAsync("Dune", scifi.Id);
        await db.Store.CreateNoteAsync("Index", books.Id);
        var keep = await db.Store.CreateNoteAsync("Keep me", null);

        await db.Store.DeleteFolderAsync(books.Id);

        var remaining = await db.Store.GetTreeAsync();
        Assert.Equal([keep.Id], remaining.Select(e => e.Id));
    }

    [Fact]
    public async Task Moving_a_folder_into_its_own_descendant_is_rejected()
    {
        await using var db = await TestDb.CreateAsync();
        var a = await db.Store.CreateFolderAsync("A", null);
        var b = await db.Store.CreateFolderAsync("B", a.Id);
        var c = await db.Store.CreateFolderAsync("C", b.Id);

        await Assert.ThrowsAsync<DomainException>(() => db.Store.MoveFolderAsync(a.Id, c.Id));
        await Assert.ThrowsAsync<DomainException>(() => db.Store.MoveFolderAsync(a.Id, a.Id));

        await db.Store.MoveFolderAsync(c.Id, null); // moving up is fine
        Assert.Null((await db.Store.GetTreeAsync()).Single(e => e.Id == c.Id).ParentId);
    }

    [Fact]
    public async Task Names_are_trimmed_and_blank_names_rejected()
    {
        await using var db = await TestDb.CreateAsync();
        var folder = await db.Store.CreateFolderAsync("  Books  ", null);
        Assert.Equal("Books", folder.Name);

        await Assert.ThrowsAsync<DomainException>(() => db.Store.CreateFolderAsync("   ", null));
        await Assert.ThrowsAsync<DomainException>(() => db.Store.CreateNoteAsync("", null));
    }

    [Fact]
    public async Task Creating_in_a_missing_folder_is_rejected()
    {
        await using var db = await TestDb.CreateAsync();
        await Assert.ThrowsAsync<DomainException>(() => db.Store.CreateNoteAsync("x", Guid.NewGuid()));
    }

    [Fact]
    public async Task Recent_notes_are_ordered_by_last_edit()
    {
        await using var db = await TestDb.CreateAsync();
        var first = await db.Store.CreateNoteAsync("First", null);
        await db.Store.CreateNoteAsync("Second", null);
        await Task.Delay(5);
        await db.Store.SaveContentAsync(first.Id, "edited");

        var recent = await db.Store.GetRecentNotesAsync(10);

        Assert.Equal("First", recent[0].Title);
    }

    [Fact]
    public async Task Tree_changes_raise_event_but_content_saves_do_not()
    {
        await using var db = await TestDb.CreateAsync();
        var count = 0;
        db.Store.TreeChanged += () => count++;

        var note = await db.Store.CreateNoteAsync("n", null);
        await db.Store.SaveContentAsync(note.Id, "hello");

        Assert.Equal(1, count);
    }
}
