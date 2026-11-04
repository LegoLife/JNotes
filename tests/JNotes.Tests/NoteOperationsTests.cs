using JNotes.Core;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests;

public class NoteOperationsTests
{
    [Fact]
    public async Task Resolves_notes_by_id_title_and_path_and_reports_ambiguity()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var dune = await db.Store.CreateNoteAsync("Index", books.Id);
        var other = await db.Store.CreateNoteAsync("Index", null);

        Assert.Equal(dune.Id, (await db.Ops.ResolveNoteAsync(dune.Id.ToString())).Id);
        Assert.Equal(dune.Id, (await db.Ops.ResolveNoteAsync("books/index")).Id);
        var ex = await Assert.ThrowsAsync<DomainException>(() => db.Ops.ResolveNoteAsync("index"));
        Assert.Contains(other.Id.ToString(), ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => db.Ops.ResolveNoteAsync("missing"));
    }

    [Fact]
    public async Task Create_note_makes_missing_folders_and_applies_template()
    {
        await using var db = await TestDb.CreateAsync();

        var note = await db.Ops.CreateNoteAsync("Chapter 5", "Books/Dune", "book chapter", "Extra line");

        var index = new NoteTreeIndex(await db.Store.GetTreeAsync());
        Assert.Equal("Books / Dune", index.PathOf(note.FolderId!.Value));
        var content = (await db.Store.GetNoteAsync(note.Id))!.Content;
        Assert.Contains("**Book:** Dune", content);          // template rendered with folder name
        Assert.Contains(":::ai-request{id=\"summary\"}", content); // Step3 seed added an AI request
        Assert.EndsWith("Extra line\n", content);
    }

    [Fact]
    public async Task Fulfilling_a_request_saves_and_raises_a_live_edit()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Ops.CreateNoteAsync("Ch 1", "Books/Dune", "Book chapter", null);
        NoteEdit? raised = null;
        db.Events.ExternalEdit += e => raised = e;

        var pending = await db.Ops.ListPendingRequestsAsync(null);
        var request = Assert.Single(pending);
        Assert.Equal(("summary", "Summary", "Books / Dune"), (request.RequestId, request.Section, request.NotePath));

        await db.Ops.FulfillRequestAsync(note.Id.ToString(), "summary", "It begins.");

        Assert.Empty(await db.Ops.ListPendingRequestsAsync(null));
        var edit = Assert.IsType<RequestFulfilledEdit>(raised);
        Assert.Equal(note.Id, edit.NoteId);
        Assert.Contains("It begins.", (await db.Store.GetNoteAsync(note.Id))!.Content);
        Assert.Equal(edit.NewContent, (await db.Store.GetNoteAsync(note.Id))!.Content);
    }

    [Fact]
    public async Task Replace_section_and_append_edit_the_stored_note()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Store.CreateNoteAsync("N", null, "## Summary\n\nold\n\n## Other\n\nkeep\n");

        await db.Ops.ReplaceSectionAsync("N", "Summary", "new");
        await db.Ops.AppendAsync("N", "## Added\n\nend");

        Assert.Equal("## Summary\n\nnew\n\n## Other\n\nkeep\n\n## Added\n\nend\n", (await db.Store.GetNoteAsync(note.Id))!.Content);
    }

    [Fact]
    public async Task Move_note_resolves_folder_paths_and_top_level()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Store.CreateNoteAsync("N", null);

        var (_, path) = await db.Ops.MoveNoteAsync("N", "Archive/2026");
        Assert.Equal("Archive / 2026", path);

        await db.Ops.MoveNoteAsync(note.Id.ToString(), "");
        Assert.Null((await db.Store.GetNoteAsync(note.Id))!.FolderId);
    }

    [Fact]
    public async Task Commands_are_seeded_and_get_unique_names()
    {
        await using var db = await TestDb.CreateAsync();
        Assert.NotNull(await db.Commands.GetByNameAsync("process-requests"));

        var a = await db.Commands.CreateAsync("Chapter summary", "", "x");
        Assert.Equal("chapter-summary-2", a.Name);
    }
}
