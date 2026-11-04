using JNotes.Core;

namespace JNotes.Tests;

public class SearchQueryTests
{
    [Theory]
    [InlineData("spice", "\"spice\"*")]
    [InlineData("  Paul  atreides ", "\"Paul\"* \"atreides\"*")]
    [InlineData("\"reverend mother\" paul", "\"reverend mother\" \"paul\"*")]
    [InlineData("don't", "\"don\"* \"t\"*")]
    [InlineData("AND OR NOT (x*) NEAR", "\"AND\"* \"OR\"* \"NOT\"* \"x\"* \"NEAR\"*")] // operators neutralised
    public void Builds_safe_prefix_queries(string input, string expected) =>
        Assert.Equal(expected, Search.BuildFtsQuery(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*** \"\" ()")]
    public void Returns_null_when_nothing_searchable(string input) => Assert.Null(Search.BuildFtsQuery(input));

    [Fact]
    public void Snippets_split_into_highlighted_runs()
    {
        var runs = Search.SplitHighlights($"a {Search.HighlightStart}spice{Search.HighlightEnd} b").ToList();
        Assert.Equal([("a ", false), ("spice", true), (" b", false)], runs);
    }

    [Fact]
    public void Snippet_cleaning_strips_markdown_but_keeps_text()
    {
        Assert.Equal("Chapter 1 Paul meets the Reverend Mother",
            Search.CleanSnippet("## Chapter 1\n\nPaul meets the **Reverend Mother** ![x](/attachments/a.png)"));
        Assert.Equal("see Dune", Search.CleanSnippet("see [Dune](/notes/1)"));
    }

    [Fact]
    public void Snippet_cleaning_flattens_tables_and_checklists()
    {
        var md = "| Name | Role |\n| ---- | ---- |\n| Ryland Grace | Teacher |\n\n- [x] Chapter 1\n- [ ] Chapter 2";
        Assert.Equal("Name Role Ryland Grace Teacher Chapter 1 Chapter 2", Search.CleanSnippet(md));
    }
}

public class NoteSearchTests
{
    [Fact]
    public async Task Finds_notes_by_word_prefix_in_body_and_title()
    {
        await using var db = await TestDb.CreateAsync();
        var dune = await db.Store.CreateNoteAsync("Dune", null, "Paul meets the Reverend Mother on Arrakis.");
        await db.Store.CreateNoteAsync("Shopping", null, "milk, eggs");

        var hits = await db.Search.SearchAsync("arrak");

        var hit = Assert.Single(hits);
        Assert.Equal(dune.Id, hit.NoteId);
        Assert.Contains($"{Search.HighlightStart}Arrakis{Search.HighlightEnd}", hit.Snippet);
        Assert.Single(await db.Search.SearchAsync("dun"));
    }

    [Fact]
    public async Task Title_matches_rank_above_body_matches()
    {
        await using var db = await TestDb.CreateAsync();
        await db.Store.CreateNoteAsync("Shopping list", null, "remember to buy a spice rack for the kitchen");
        await db.Store.CreateNoteAsync("Spice", null, "The spice must flow.");

        var hits = await db.Search.SearchAsync("spice");

        Assert.Equal("Spice", hits[0].Title);
    }

    [Fact]
    public async Task All_words_must_match()
    {
        await using var db = await TestDb.CreateAsync();
        await db.Store.CreateNoteAsync("a", null, "desert planet");
        await db.Store.CreateNoteAsync("b", null, "desert island");

        Assert.Equal("a", Assert.Single(await db.Search.SearchAsync("desert planet")).Title);
        Assert.Empty(await db.Search.SearchAsync("\"planet desert\""));
    }

    [Fact]
    public async Task Index_follows_edits_renames_and_deletes()
    {
        await using var db = await TestDb.CreateAsync();
        var folder = await db.Store.CreateFolderAsync("Books", null);
        var note = await db.Store.CreateNoteAsync("Draft", folder.Id, "old words");

        await db.Store.SaveContentAsync(note.Id, "fresh words");
        await db.Store.RenameNoteAsync(note.Id, "Final");
        Assert.Empty(await db.Search.SearchAsync("old"));
        Assert.Single(await db.Search.SearchAsync("fresh"));
        Assert.Single(await db.Search.SearchAsync("final"));

        await db.Store.DeleteFolderAsync(folder.Id); // cascade delete must also clear the index
        Assert.Empty(await db.Search.SearchAsync("fresh"));
    }

    [Fact]
    public async Task Folder_filter_includes_subfolders_only()
    {
        await using var db = await TestDb.CreateAsync();
        var books = await db.Store.CreateFolderAsync("Books", null);
        var scifi = await db.Store.CreateFolderAsync("Sci-fi", books.Id);
        await db.Store.CreateNoteAsync("Dune", scifi.Id, "sand");
        await db.Store.CreateNoteAsync("Beach trip", null, "sand");

        var hits = await db.Search.SearchAsync("sand", books.Id);

        Assert.Equal("Dune", Assert.Single(hits).Title);
    }
}
