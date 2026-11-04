using System.IO.Compression;
using System.Text;
using System.Text.Json;
using JNotes.Core;

namespace JNotes.Tests;

public class ProseMirrorMarkdownTests
{
    private static string Md(string json, Func<string, string?>? image = null, Func<string, string>? link = null) =>
        new ProseMirrorMarkdown(image ?? (s => s), link ?? (h => h)).Convert(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Converts_headings_marks_links_and_breaks()
    {
        var md = Md("""
            {"type":"doc","content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Plan"}]},
              {"type":"paragraph","content":[
                {"type":"text","text":"Read "},
                {"type":"text","marks":[{"type":"bold"}],"text":"Zero "},
                {"type":"text","marks":[{"type":"bold"},{"type":"textStyle","attrs":{"color":"red"}}],"text":"to One"},
                {"type":"text","text":" by "},
                {"type":"text","marks":[{"type":"link","attrs":{"href":"https://x.com/a b"}}],"text":"Thiel"},
                {"type":"hardBreak"},
                {"type":"text","marks":[{"type":"underline"}],"text":"2*3 = 6_ok"}]},
              {"type":"paragraph"},
              {"type":"horizontalRule"}]}
            """);

        Assert.Equal("## Plan\n\nRead **Zero to One** by [Thiel](https://x.com/a%20b)\\\n2\\*3 = 6\\_ok\n\n---\n", md);
    }

    [Fact]
    public void Converts_nested_lists_tasks_and_ordered_lists_tightly()
    {
        var md = Md("""
            {"type":"doc","content":[
              {"type":"bulletList","content":[
                {"type":"listItem","content":[
                  {"type":"paragraph","content":[{"type":"text","text":"Fruit"}]},
                  {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Apple"}]}]}]}]},
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Veg"}]}]}]},
              {"type":"orderedList","attrs":{"start":3},"content":[
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"third"}]}]}]},
              {"type":"taskList","content":[
                {"type":"taskItem","attrs":{"checked":true},"content":[{"type":"paragraph","content":[{"type":"text","text":"done"}]}]},
                {"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"todo"}]}]}]}]}
            """);

        Assert.Equal("- Fruit\n  - Apple\n- Veg\n\n3. third\n\n- [x] done\n- [ ] todo\n", md);
    }

    [Fact]
    public void Converts_tables_images_embeds_and_quotes()
    {
        var md = Md("""
            {"type":"doc","content":[
              {"type":"table","content":[
                {"type":"tableRow","content":[
                  {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Name"}]}]},
                  {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Note"}]}]}]},
                {"type":"tableRow","content":[
                  {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"A|B"}]}]},
                  {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"x"},{"type":"hardBreak"},{"type":"text","text":"y"}]}]}]}]},
              {"type":"image","attrs":{"src":"/api/files/1/a.png","alt":"chart"}},
              {"type":"image","attrs":{"src":"Old%20Notion/Untitled.png","alt":"Untitled"}},
              {"type":"embed","attrs":{"src":"https://www.youtube.com/watch?v=hJbwyN4ZoCg","provider":"youtube"}},
              {"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"wise"}]}]}]}
            """, image: s => s.StartsWith("/api") ? "files/1/a.png" : null);

        Assert.Equal(
            "| Name | Note |\n| --- | --- |\n| A\\|B | x y |\n\n![chart](files/1/a.png)\n\n*(missing image)*\n\n" +
            "https://www.youtube.com/watch?v=hJbwyN4ZoCg\n\n> wise\n", md);
    }

    [Fact]
    public void Paragraphs_that_look_like_markdown_blocks_are_escaped()
    {
        var md = Md("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"- not a list"}]}]}""");
        Assert.Equal("\\- not a list\n", md);
    }
}

public class DocmostDbConverterTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static DocmostPage Page(string title, string space, string json, Guid? id = null, string? slug = null, DateTimeOffset? updated = null) =>
        new(id ?? Guid.NewGuid(), slug ?? Guid.NewGuid().ToString("N")[..10], title, null, space,
            JsonDocument.Parse(json).RootElement.Clone(), new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
            updated ?? new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Database_dump_imports_with_spaces_images_links_and_original_dates()
    {
        var attachmentId = Guid.NewGuid();
        var chores = Page("Kids chore chart", "Random Topics", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Mon: dishes"}]}]}""", slug: "aax8rM9ERF");
        var plan = Page("Plan", "General", $$$"""
            {"type":"doc","content":[
              {"type":"paragraph","content":[{"type":"text","marks":[{"type":"link","attrs":{"href":"https://docmost.example.org/s/RT/p/kids-chore-chart-aax8rM9ERF"}}],"text":"chores"}]},
              {"type":"paragraph","content":[{"type":"text","marks":[{"type":"link","attrs":{"href":"../Kids%20chore%20chart%20301b8e2814384baea5aceb6702fb914b.md"}}],"text":"old notion link"}]},
              {"type":"image","attrs":{"src":"/api/files/{{{attachmentId}}}/image.png","alt":""}},
              {"type":"image","attrs":{"src":"Gone/Untitled.png","alt":"Untitled"}}]}
            """, updated: new DateTimeOffset(2025, 5, 5, 12, 0, 0, TimeSpan.Zero));
        var untitled = new DocmostPage(Guid.NewGuid(), "zzzzzz1234", null, null, "General", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var attachments = new List<DocmostAttachment> { new(attachmentId, "image.png", $"{Workspace}/files/{attachmentId}/image.png") };

        using var zipStream = new MemoryStream();
        var result = await DocmostDbConverter.WriteZipAsync([chores, plan, untitled], attachments,
            path => path.EndsWith("image.png") ? new MemoryStream(AttachmentStoreTests.Png) : null, zipStream);
        Assert.Equal((3, 1), (result.Pages, result.Images));
        Assert.Single(result.Warnings); // the already-broken Notion image

        await using var db = await TestDb.CreateAsync();
        zipStream.Position = 0;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var report = await new DocmostImporter(db.Store, db.Attachments).ImportAsync(zip, null);
        Assert.Empty(report.Warnings);

        var index = new NoteTreeIndex(await db.Store.GetTreeAsync());
        Assert.Equal(["General", "Random Topics"], index.ChildrenOf(null).Where(e => e.IsFolder).Select(e => e.Name).Order());
        var general = index.ChildrenOf(null).Single(e => e.Name == "General");
        Assert.Equal(["Plan", "Untitled"], index.ChildrenOf(general.Id).Select(e => e.Name).Order());

        var choresId = index.ChildrenOf(index.ChildrenOf(null).Single(e => e.Name == "Random Topics").Id).Single().Id;
        var planNote = (await db.Store.GetNoteAsync(index.ChildrenOf(general.Id).Single(e => e.Name == "Plan").Id))!;
        Assert.Contains($"[chores](/notes/{choresId})", planNote.Content);           // Docmost URL link
        Assert.Contains($"[old notion link](/notes/{choresId})", planNote.Content);  // Notion-style .md link
        Assert.Matches(@"!\[\]\(/attachments/[0-9a-f]{32}\.png\)", planNote.Content);
        Assert.Contains("*(missing image)*", planNote.Content);
        Assert.Equal(new DateTimeOffset(2025, 5, 5, 12, 0, 0, TimeSpan.Zero), planNote.UpdatedAt); // original date kept
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), planNote.CreatedAt);
    }
}
