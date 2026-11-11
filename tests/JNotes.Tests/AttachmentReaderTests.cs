using System.IO.Compression;
using System.Text;
using JNotes.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace JNotes.Tests;

public class AttachmentReaderTests
{
    private static async Task<string> SaveAsync(TestDb db, byte[] bytes, string name)
    {
        var stored = await db.Attachments.SaveFileAsync(new MemoryStream(bytes), name);
        return AttachmentStore.UrlPrefix + stored[..33] + Uri.EscapeDataString(stored[33..]);
    }

    [Fact]
    public void Finds_image_and_file_links_once_each()
    {
        var md = "![cover](/attachments/0123456789abcdef0123456789abcdef.png)\n[📎 My Doc.pdf](/attachments/0123456789abcdef0123456789abcdef/My%20Doc.pdf) "
                 + "[again](/attachments/0123456789abcdef0123456789abcdef/My%20Doc.pdf) [web](https://example.com)";
        var links = AttachmentReader.FindLinks(md);
        Assert.Equal(["cover", "My Doc.pdf"], links.Select(l => l.Name));
    }

    [Fact]
    public async Task Reads_images_as_images()
    {
        await using var db = await TestDb.CreateAsync();
        var name = await db.Attachments.SaveAsync(new MemoryStream(AttachmentStoreTests.Png));
        var result = await new AttachmentReader(db.Attachments).ReadAsync("http://pi:8080/attachments/" + name);
        var image = Assert.IsType<AttachmentContent.Image>(result);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(AttachmentStoreTests.Png, image.Data);
    }

    [Fact]
    public async Task Reads_pdf_text()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Invoice total 42 dollars", 12, new PdfPoint(50, 700), font);
        builder.AddPage(PageSize.A4).AddText("Second page here", 12, new PdfPoint(50, 700), font);
        await using var db = await TestDb.CreateAsync();
        var url = await SaveAsync(db, builder.Build(), "Invoice.pdf");

        var text = Assert.IsType<AttachmentContent.Text>(await new AttachmentReader(db.Attachments).ReadAsync(url));
        Assert.Contains("--- Page 1 ---", text.Content);
        Assert.Contains("Invoice total 42 dollars", text.Content);
        Assert.Contains("Second page here", text.Content);
    }

    [Fact]
    public async Task Reads_docx_text()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        await using (var s = await zip.CreateEntry("word/document.xml").OpenAsync())
            await s.WriteAsync("<w:document><w:body><w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:t xml:space=\"preserve\"> &amp; bye</w:t></w:r></w:p><w:p><w:r><w:t>Line 2</w:t></w:r></w:p></w:body></w:document>"u8.ToArray());
        await using var db = await TestDb.CreateAsync();
        var url = await SaveAsync(db, buffer.ToArray(), "letter.docx");

        var text = Assert.IsType<AttachmentContent.Text>(await new AttachmentReader(db.Attachments).ReadAsync(url));
        Assert.Equal("Hello & bye\nLine 2\n", text.Content);
    }

    [Fact]
    public async Task Pages_long_text_and_sniffs_unknown_extensions()
    {
        await using var db = await TestDb.CreateAsync();
        var body = new string('a', AttachmentReader.MaxChars) + "TAIL";
        var url = await SaveAsync(db, Encoding.UTF8.GetBytes(body), "notes.weird");
        var reader = new AttachmentReader(db.Attachments);

        var first = Assert.IsType<AttachmentContent.Text>(await reader.ReadAsync(url));
        Assert.Equal((AttachmentReader.MaxChars, body.Length), (first.Content.Length, first.Total));
        var rest = Assert.IsType<AttachmentContent.Text>(await reader.ReadAsync(url, AttachmentReader.MaxChars));
        Assert.Equal("TAIL", rest.Content);
    }

    [Fact]
    public async Task Binary_files_are_unreadable_and_bad_urls_fail()
    {
        await using var db = await TestDb.CreateAsync();
        var url = await SaveAsync(db, [0x50, 0x4B, 0x03, 0x04, 0, 0, 1, 2], "photos.zip");
        var reader = new AttachmentReader(db.Attachments);

        Assert.IsType<AttachmentContent.Unreadable>(await reader.ReadAsync(url));
        await Assert.ThrowsAsync<DomainException>(() => reader.ReadAsync("https://example.com/x.pdf"));
        await Assert.ThrowsAsync<DomainException>(() => reader.ReadAsync("/attachments/../jnotes.db"));
    }
}
