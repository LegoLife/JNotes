using System.IO.Compression;
using System.Text;
using JNotes.Core;

namespace JNotes.Tests;

public class ExportTests
{
    private static async Task<Dictionary<string, string>> ReadZip(Stream zipStream)
    {
        zipStream.Position = 0;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var result = new Dictionary<string, string>();
        foreach (var e in zip.Entries)
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            result[e.FullName] = await reader.ReadToEndAsync();
        }
        return result;
    }

    [Fact]
    public async Task Vault_mirrors_folders_and_rewrites_images_and_links()
    {
        await using var db = await TestDb.CreateAsync();
        var image = await db.Attachments.SaveAsync(new MemoryStream(AttachmentStoreTests.Png));
        var books = await db.Store.CreateFolderAsync("Books", null);
        var dune = await db.Store.CreateFolderAsync("Dune", books.Id);
        var ch1 = await db.Store.CreateNoteAsync("Chapter 1: Arrival", dune.Id, $"![cover](/attachments/{image})");
        await db.Store.CreateNoteAsync("Index", null, $"See [chapter one](/notes/{ch1.Id}).");
        await db.Store.CreateNoteAsync("Index", null, "duplicate title");
        var trashed = await db.Store.CreateNoteAsync("Gone", null, "x");
        await db.Store.DeleteNoteAsync(trashed.Id);

        using var zip = new MemoryStream();
        await new Exporter(db.Store, db.Attachments).WriteVaultAsync(zip);
        var files = await ReadZip(zip);

        Assert.Equal(["Books/Dune/Chapter 1- Arrival.md", "Index (2).md", "Index.md", $"attachments/{image}"],
            files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal($"# Chapter 1: Arrival\n\n![cover](../../attachments/{image})\n", files["Books/Dune/Chapter 1- Arrival.md"]);
        var index = files["Index.md"].Contains("See") ? files["Index.md"] : files["Index (2).md"];
        Assert.Contains("[chapter one](Books/Dune/Chapter%201-%20Arrival.md)", index);
        Assert.DoesNotContain(files.Keys, k2 => k2.Contains("Gone"));   // Trash isn't exported
    }

    [Fact]
    public async Task Exported_vault_reimports_with_the_same_structure()
    {
        await using var source = await TestDb.CreateAsync();
        var image = await source.Attachments.SaveAsync(new MemoryStream(AttachmentStoreTests.Png));
        var books = await source.Store.CreateFolderAsync("Books", null);
        var dune = await source.Store.CreateNoteAsync("Dune", books.Id, $"## Notes\n\n- spice\n\n![map](/attachments/{image})\n");
        await source.Store.CreateNoteAsync("Reading list", null, $"- [Dune](/notes/{dune.Id})\n");
        using var zipStream = new MemoryStream();
        await new Exporter(source.Store, source.Attachments).WriteVaultAsync(zipStream);

        await using var target = await TestDb.CreateAsync();
        zipStream.Position = 0;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var report = await new DocmostImporter(target.Store, target.Attachments).ImportAsync(zip, null);

        Assert.Empty(report.Warnings);
        var index = new NoteTreeIndex(await target.Store.GetTreeAsync());
        var newBooks = index.ChildrenOf(null).Single(e => e.IsFolder);
        var newDune = index.ChildrenOf(newBooks.Id).Single();
        Assert.Equal(("Books", "Dune"), (newBooks.Name, newDune.Name));
        var duneContent = (await target.Store.GetNoteAsync(newDune.Id))!.Content;
        Assert.StartsWith("## Notes\n\n- spice", duneContent);
        Assert.Matches(@"!\[map\]\(/attachments/[0-9a-f]{32}\.png\)", duneContent);
        var list = index.ChildrenOf(null).Single(e => e.Name == "Reading list");
        Assert.Contains($"[Dune](/notes/{newDune.Id})", (await target.Store.GetNoteAsync(list.Id))!.Content);
    }

    [Fact]
    public async Task Single_note_is_markdown_or_a_zip_when_it_has_images()
    {
        await using var db = await TestDb.CreateAsync();
        var exporter = new Exporter(db.Store, db.Attachments);
        var plain = await db.Store.CreateNoteAsync("What/If?", null, "text");
        var image = await db.Attachments.SaveAsync(new MemoryStream(AttachmentStoreTests.Png));
        var withImage = await db.Store.CreateNoteAsync("Pics", null, $"![x](/attachments/{image})");

        var md = await exporter.ExportNoteAsync(plain.Id);
        Assert.Equal(("What-If-.md", "# What/If?\n\ntext\n"), (md.FileName, Encoding.UTF8.GetString(md.Data)));

        var zipped = await exporter.ExportNoteAsync(withImage.Id);
        Assert.Equal("Pics.zip", zipped.FileName);
        var files = await ReadZip(new MemoryStream(zipped.Data));
        Assert.Equal($"# Pics\n\n![x](attachments/{image})\n", files["Pics.md"]);
        Assert.True(files.ContainsKey($"attachments/{image}"));
    }

    [Theory]
    [InlineData("a/b/c.md", "a/d.md", "../d.md")]
    [InlineData("x.md", "a/b/y.md", "a/b/y.md")]
    [InlineData("a/x.md", "a/y.md", "y.md")]
    public void Relative_paths_between_notes(string from, string to, string expected) =>
        Assert.Equal(expected, Exporter.RelativePath(from, to));
}

public class DocmostConversionTests
{
    [Fact]
    public void Callouts_become_labelled_quotes()
    {
        var md = "Before\n\n:::warning\nDon't **panic**\n\nSecond para\n:::\n\nAfter";
        Assert.Equal("Before\n\n> **⚠️ Warning**\n>\n> Don't **panic**\n>\n> Second para\n\nAfter", DocmostImporter.ConvertCallouts(md));
    }

    [Fact]
    public void Callout_conversion_leaves_ai_blocks_alone()
    {
        const string md = ":::ai-request{id=\"x\"}\nprompt\n:::";
        Assert.Equal(md, DocmostImporter.ConvertCallouts(md));
    }

    [Fact]
    public void Toggles_become_bold_titles_including_nested_ones()
    {
        var md = "<details markdown=\"1\">\n<summary>Outer</summary>\n\nText\n\n<details markdown=\"1\">\n<summary>Inner</summary>\n\nDeep\n\n</details>\n\n</details>";
        var result = DocmostImporter.ConvertDetails(md);
        Assert.DoesNotContain("<details", result);
        Assert.Contains("**▸ Outer**", result);
        Assert.Contains("**▸ Inner**\n\nDeep", result);
    }
}
