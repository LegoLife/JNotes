using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace JNotes.Core;

/// <summary>What an attachment holds, in a form an AI client can use.</summary>
public abstract record AttachmentContent(string Name, string Url, long Size)
{
    public sealed record Image(string Name, string Url, long Size, byte[] Data, string MimeType) : AttachmentContent(Name, Url, Size);
    /// <summary>Extracted text; <see cref="Offset"/>/<see cref="Total"/> are character positions for paging long files.</summary>
    public sealed record Text(string Name, string Url, long Size, string Content, int Offset, int Total) : AttachmentContent(Name, Url, Size);
    public sealed record Unreadable(string Name, string Url, long Size, string Reason) : AttachmentContent(Name, Url, Size);
}

/// <summary>Finds attachments in note markdown and turns them into images or text (PDF, Word, plain-text formats).</summary>
public sealed partial class AttachmentReader(AttachmentStore store)
{
    public const int MaxChars = 50_000;
    public const long MaxImageBytes = 5 * 1024 * 1024;

    public sealed record Link(string Name, string Url);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".toml", ".html", ".htm",
        ".cs", ".js", ".ts", ".py", ".sql", ".sh", ".ps1", ".css", ".razor", ".java", ".go", ".rs", ".c", ".cpp", ".h", ".srt", ".vtt",
    };

    /// <summary>Every image and file attachment linked from a note, in order, without duplicates.</summary>
    public static IReadOnlyList<Link> FindLinks(string markdown)
    {
        var seen = new HashSet<string>();
        var result = new List<Link>();
        foreach (Match m in LinkPattern().Matches(markdown))
        {
            var url = m.Groups["url"].Value;
            if (!seen.Add(url)) continue;
            var text = m.Groups["text"].Value.Replace("📎", "").Trim();
            var name = Split(url)?.Name;
            result.Add(new Link(text.Length > 0 ? text : name ?? url, url));
        }
        return result;
    }

    /// <summary>Reads an attachment by its URL as it appears in a note (absolute http URLs to this app also work).</summary>
    public Task<AttachmentContent> ReadAsync(string url, int offset = 0, CancellationToken ct = default) => ReadAsync(url, offset, MaxChars, ct);

    /// <summary>As above, returning at most <paramref name="maxChars"/> characters of text.</summary>
    public async Task<AttachmentContent> ReadAsync(string url, int offset, int maxChars, CancellationToken ct = default)
    {
        var (id, name) = Split(url) ?? throw new DomainException($"“{url}” isn't a JNotes attachment link (expected /attachments/…).");
        var path = (id is null ? store.GetPath(name) : store.GetFilePath(id, name))
                   ?? throw new DomainException($"Attachment “{name}” wasn't found.");
        var clean = AttachmentStore.UrlPrefix + (id is null ? name : $"{id}/{Uri.EscapeDataString(name)}");
        var size = new FileInfo(path).Length;
        var ext = Path.GetExtension(name).ToLowerInvariant();

        if (id is null || ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
        {
            var head = new byte[16];
            int n;
            await using (var fs = File.OpenRead(path)) n = await fs.ReadAtLeastAsync(head, head.Length, false, ct);
            if (AttachmentStore.SniffExtension(head.AsSpan(0, n)) is { } kind)
            {
                if (size > MaxImageBytes)
                    return new AttachmentContent.Unreadable(name, clean, size, $"Image is too large to send ({size / 1024 / 1024} MB; limit {MaxImageBytes / 1024 / 1024} MB).");
                return new AttachmentContent.Image(name, clean, size, await File.ReadAllBytesAsync(path, ct), AttachmentStore.ContentTypeFor("x." + kind));
            }
        }

        string? text;
        try
        {
            text = ext switch
            {
                ".pdf" => ExtractPdf(path),
                ".docx" => ExtractDocx(path),
                ".epub" => ExtractEpub(path),
                _ when TextExtensions.Contains(ext) || LooksLikeText(path) => await File.ReadAllTextAsync(path, ct),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AttachmentContent.Unreadable(name, clean, size, $"Couldn't extract text: {ex.Message}");
        }
        if (text is null)
            return new AttachmentContent.Unreadable(name, clean, size, $"“{ext}” files can't be read as text or images.");
        if (text.Trim().Length == 0)
            return new AttachmentContent.Unreadable(name, clean, size, "No text found (e.g. a scanned PDF, which holds only images).");

        offset = Math.Clamp(offset, 0, text.Length);
        var slice = text.Substring(offset, Math.Min(maxChars, text.Length - offset));
        return new AttachmentContent.Text(name, clean, size, slice, offset, text.Length);
    }

    /// <summary>"/attachments/{guid}.{ext}" → (null, name); "/attachments/{guid}/{name}" → (guid, decoded name).</summary>
    private static (string? Id, string Name)? Split(string url)
    {
        var u = url.Trim().Trim('<', '>');
        var at = u.IndexOf(AttachmentStore.UrlPrefix, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = u[(at + AttachmentStore.UrlPrefix.Length)..].Split('?', '#')[0];
        var slash = rest.IndexOf('/');
        static string Decode(string s) { try { return Uri.UnescapeDataString(s); } catch (UriFormatException) { return s; } }
        return slash < 0 ? (null, Decode(rest)) : (rest[..slash], Decode(rest[(slash + 1)..]));
    }

    private static string ExtractPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            sb.Append("--- Page ").Append(page.Number).Append(" ---\n");
            sb.Append(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string? ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        var sb = new StringBuilder();
        foreach (Match p in DocxParagraph().Matches(xml))
        {
            foreach (Match t in DocxText().Matches(p.Value))
                sb.Append(System.Net.WebUtility.HtmlDecode(t.Groups[1].Value));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>EPUB: container.xml → OPF package → spine order → each XHTML chapter as plain text.</summary>
    private static string? ExtractEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string Read(string name)
        {
            var e = zip.GetEntry(name) ?? throw new InvalidDataException($"EPUB is missing {name}.");
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }
        var container = System.Xml.Linq.XDocument.Parse(Read("META-INF/container.xml"));
        var opfPath = container.Descendants().First(e => e.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var opfDir = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        var opf = System.Xml.Linq.XDocument.Parse(Read(opfPath));

        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .Where(e => e.Attribute("id") is not null && e.Attribute("href") is not null)
            .ToDictionary(e => e.Attribute("id")!.Value, e => e.Attribute("href")!.Value);
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref")
            .Select(e => e.Attribute("idref")?.Value).OfType<string>()
            .Where(manifest.ContainsKey).Select(id => manifest[id]);

        var title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("# ").Append(title.Trim()).Append("\n\n");
        foreach (var href in spine)
        {
            var entryPath = NormalizeZipPath(opfDir + Uri.UnescapeDataString(href.Split('#')[0]));
            if (zip.GetEntry(entryPath) is null) continue;
            var text = HtmlToText(Read(entryPath));
            if (text.Length > 0) sb.Append(text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string NormalizeZipPath(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static string HtmlToText(string html)
    {
        html = HtmlDropped().Replace(html, "");
        html = HtmlBlockEnd().Replace(html, "\n");
        html = HtmlTag().Replace(html, "");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = HorizontalSpace().Replace(html, " ");
        html = BlankLines().Replace(html, "\n\n");
        return string.Join('\n', html.Split('\n').Select(l => l.Trim())).Trim();
    }

    [GeneratedRegex(@"<(head|script|style)\b[\s\S]*?</\1>", RegexOptions.IgnoreCase)] private static partial Regex HtmlDropped();
    [GeneratedRegex(@"<br\s*/?>|</(p|div|h[1-6]|li|tr|blockquote|section|pre)>", RegexOptions.IgnoreCase)] private static partial Regex HtmlBlockEnd();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"[ \t\r\f\v ]+")] private static partial Regex HorizontalSpace();
    [GeneratedRegex(@"\n\s*\n\s*\n+")] private static partial Regex BlankLines();

    // Unknown extension: text if the first 8 KB is valid UTF-8 with no NULs.
    private static bool LooksLikeText(string path)
    {
        var buffer = new byte[8192];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var span = buffer.AsSpan(0, n);
        if (span.Contains((byte)0)) return false;
        // Ignore a multi-byte character cut off at the 8 KB boundary.
        var end = span.Length;
        for (var i = 1; i <= 3 && i <= span.Length; i++)
        {
            var c = span[^i];
            if ((c & 0xC0) == 0x80) continue;
            if (c >= 0xC0) end = span.Length - i;
            break;
        }
        try { new UTF8Encoding(false, true).GetCharCount(span[..end]); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\((?<url>/attachments/[^)\s]+)\)")] private static partial Regex LinkPattern();
    [GeneratedRegex(@"<w:p[ >][\s\S]*?</w:p>")] private static partial Regex DocxParagraph();
    [GeneratedRegex(@"<w:t(?:\s[^>]*)?>([^<]*)</w:t>")] private static partial Regex DocxText();
}
