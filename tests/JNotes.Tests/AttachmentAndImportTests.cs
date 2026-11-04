using System.IO.Compression;
using System.Text;
using JNotes.Core;

namespace JNotes.Tests;

public class AttachmentStoreTests
{
    internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    [Fact]
    public async Task Saves_images_with_sniffed_extension()
    {
        await using var db = await TestDb.CreateAsync();
        var name = await db.Attachments.SaveAsync(new MemoryStream(Png));

        Assert.EndsWith(".png", name);
        Assert.True(AttachmentStore.IsValidName(name));
        Assert.Equal(Png, await File.ReadAllBytesAsync(db.Attachments.GetPath(name)!));
    }

    [Fact]
    public async Task Rejects_non_images()
    {
        await using var db = await TestDb.CreateAsync();
        await Assert.ThrowsAsync<DomainException>(() =>
            db.Attachments.SaveAsync(new MemoryStream("<svg onload=alert(1)>"u8.ToArray())));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("abc.png")]
    [InlineData("0123456789abcdef0123456789abcdef.svg")]
    public void Rejects_unsafe_names(string name) => Assert.False(AttachmentStore.IsValidName(name));
}

public class DocmostImporterTests
{
    private static ZipArchive Zip(params (string Path, byte[] Bytes)[] files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in files)
                using (var s = zip.CreateEntry(path).Open()) s.Write(bytes);
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static (string, byte[]) Md(string path, string text) => (path, Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Maps_pages_with_children_to_folders_and_rewrites_images_and_links()
    {
        await using var db = await TestDb.CreateAsync();
        using var zip = Zip(
            Md("Books.md", "# Books\n\nMy reading list. See [Dune](Books/Dune.md).\n"),
            Md("Books/Dune.md", "# Dune\n\n![cover](files/0192f0aa-1111-7abc-9def-000000000001/cover.png)\n\nBack to [list](../Books.md)\n"),
            ("Books/files/0192f0aa-1111-7abc-9def-000000000001/cover.png", AttachmentStoreTests.Png),
            Md("Empty parent.md", "# Empty parent\n"),
            Md("Empty parent/Child.md", "# Child\n\ntext\n"),
            Md("Loose.md", "# Loose: a page\n\nhello\n"),
            ("docmost-metadata.json", "{}"u8.ToArray()));

        var report = await new DocmostImporter(db.Store, db.Attachments).ImportAsync(zip, null);

        var index = new NoteTreeIndex(await db.Store.GetTreeAsync());
        var books = index.ChildrenOf(null).Single(e => e is { IsFolder: true, Name: "Books" });
        var booksNote = index.ChildrenOf(books.Id).Single(e => e is { IsFolder: false, Name: "Books" });
        var dune = index.ChildrenOf(books.Id).Single(e => e.Name == "Dune");
        var emptyParent = index.ChildrenOf(null).Single(e => e.Name == "Empty parent");
        Assert.True(emptyParent.IsFolder);
        Assert.Equal(["Child"], index.ChildrenOf(emptyParent.Id).Select(e => e.Name)); // empty page -> no note
        Assert.Contains(index.ChildrenOf(null), e => e.Name == "Loose: a page");     // title from H1, not file name

        var duneContent = (await db.Store.GetNoteAsync(dune.Id))!.Content;
        Assert.DoesNotContain("# Dune", duneContent);
        Assert.Matches(@"!\[cover\]\(/attachments/[0-9a-f]{32}\.png\)", duneContent);
        Assert.Contains($"[list](/notes/{booksNote.Id})", duneContent);
        Assert.Contains($"[Dune](/notes/{dune.Id})", (await db.Store.GetNoteAsync(booksNote.Id))!.Content);

        Assert.Equal((2, 4, 1), (report.Folders, report.Notes, report.Images));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Reports_missing_files_unsupported_attachments_and_html()
    {
        await using var db = await TestDb.CreateAsync();
        using var zip = Zip(
            Md("Page.md", "# Page\n\n![gone](files/0192f0aa-1111-7abc-9def-000000000002/x.png)\n\n[spec.pdf](files/0192f0aa-1111-7abc-9def-000000000003/spec.pdf)\n\n<div data-type=\"callout\">note</div>\n"),
            ("files/0192f0aa-1111-7abc-9def-000000000003/spec.pdf", "%PDF-1.7"u8.ToArray()));

        var report = await new DocmostImporter(db.Store, db.Attachments).ImportAsync(zip, null);

        Assert.Equal(3, report.Warnings.Count);
        Assert.Contains(report.Warnings, w => w.Contains("image not found"));
        Assert.Contains(report.Warnings, w => w.Contains("spec.pdf"));
        Assert.Contains(report.Warnings, w => w.Contains("<div>"));
    }

    [Fact]
    public async Task Imports_into_a_target_folder()
    {
        await using var db = await TestDb.CreateAsync();
        var target = await db.Store.CreateFolderAsync("Imported", null);
        using var zip = Zip(Md("A.md", "# A\n\nx\n"));

        await new DocmostImporter(db.Store, db.Attachments).ImportAsync(zip, target.Id);

        Assert.Equal(target.Id, (await db.Store.GetTreeAsync()).Single(e => e.Name == "A").ParentId);
    }

    [Theory]
    [InlineData("<iframe src=\"https://www.youtube.com/embed/dQw4w9WgXcQ?si=abc\"></iframe>")]
    [InlineData("<div data-type=\"youtube\" data-src=\"https://youtu.be/dQw4w9WgXcQ\"></div>")]
    [InlineData("[https://www.youtube.com/watch?v=dQw4w9WgXcQ](https://www.youtube.com/watch?v=dQw4w9WgXcQ)")]
    public void Converts_youtube_embeds_to_bare_urls(string line) =>
        Assert.Equal("a\nhttps://www.youtube.com/watch?v=dQw4w9WgXcQ\nb", DocmostImporter.ConvertYouTube($"a\n{line}\nb"));

    [Fact]
    public void Leaves_youtube_links_inside_sentences_alone()
    {
        const string md = "Watch [this](https://youtu.be/dQw4w9WgXcQ) later";
        Assert.Equal(md, DocmostImporter.ConvertYouTube(md));
    }

    [Theory]
    [InlineData("Books", "Dune.md", "Books/Dune.md")]
    [InlineData("Books/Dune", "../Other%20Book.md", "Books/Other Book.md")]
    [InlineData("", "../escape.md", null)]
    [InlineData("A", "./files/x.png#frag", "A/files/x.png")]
    public void Resolves_relative_paths(string dir, string url, string? expected) =>
        Assert.Equal(expected, DocmostImporter.Resolve(dir, url));
}
