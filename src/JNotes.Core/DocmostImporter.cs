using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace JNotes.Core;

public sealed record ImportReport(int Folders, int Notes, int Images, IReadOnlyList<string> Warnings);

/// <summary>
/// Imports a Docmost space export (Markdown format). Docmost's layout:
/// <c>Page.md</c> + a sibling <c>Page/</c> directory holding its children; each file starts with
/// <c># Title</c>; attachments live at <c>files/&lt;id&gt;/&lt;name&gt;</c> relative to the page;
/// internal links are relative <c>.md</c> paths.
/// Mapping: a page with children becomes a folder containing the page itself as a note
/// (skipped if empty); leaf pages become notes.
/// </summary>
public sealed partial class DocmostImporter(INoteStore notes, AttachmentStore attachments)
{
    public async Task<ImportReport> ImportAsync(ZipArchive zip, Guid? targetFolderId, CancellationToken ct = default)
    {
        var run = new Run(zip);
        await ImportDirectoryAsync(run, "", targetFolderId, ct);
        await RewriteInternalLinksAsync(run, ct);
        await ApplyOriginalDatesAsync(run, ct);
        return new ImportReport(run.FolderCount, run.Created.Count, run.ImageCache.Count, run.Warnings);
    }

    private sealed class Run
    {
        public Run(ZipArchive zip)
        {
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue; // directory entry
                Files[NormalizeEntry(e.FullName)] = e;
            }
            Pages = Files.Keys.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !AttachmentPath().IsMatch(p)).ToList();
        }

        public Dictionary<string, ZipArchiveEntry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Pages { get; }
        public List<string> Warnings { get; } = [];
        public Dictionary<string, string> ImageCache { get; } = new(StringComparer.OrdinalIgnoreCase); // zip path -> url
        public Dictionary<string, Guid> NoteByPagePath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(Guid NoteId, string PagePath, string Content)> Created { get; } = [];
        public int FolderCount { get; set; }
    }

    private async Task ImportDirectoryAsync(Run run, string dir, Guid? parentFolderId, CancellationToken ct)
    {
        var pagesHere = run.Pages.Where(p => ParentDir(p).Equals(dir, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var subdirs = run.Pages.Select(p => ChildDirUnder(dir, p)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var page in pagesHere)
        {
            var childDir = page[..^3]; // "dir/Name.md" -> "dir/Name"
            var hasChildren = subdirs.Remove(subdirs.FirstOrDefault(d => d.Equals(childDir, StringComparison.OrdinalIgnoreCase)) ?? "\0");
            var (title, content) = await ReadPageAsync(run, page, ct);

            if (hasChildren)
            {
                var folder = await notes.CreateFolderAsync(title, parentFolderId, ct);
                run.FolderCount++;
                if (!string.IsNullOrWhiteSpace(content)) await CreateNoteAsync(run, page, title, folder.Id, content, ct);
                await ImportDirectoryAsync(run, childDir, folder.Id, ct);
            }
            else
            {
                await CreateNoteAsync(run, page, title, parentFolderId, content, ct);
            }
        }

        // Directories with pages but no page file of their own (not produced by Docmost, but tolerate it).
        foreach (var sub in subdirs)
        {
            var folder = await notes.CreateFolderAsync(SafeName(LastSegment(sub)), parentFolderId, ct);
            run.FolderCount++;
            await ImportDirectoryAsync(run, sub, folder.Id, ct);
        }
    }

    private async Task CreateNoteAsync(Run run, string page, string title, Guid? folderId, string content, CancellationToken ct)
    {
        var note = await notes.CreateNoteAsync(title, folderId, content, ct);
        run.NoteByPagePath[page] = note.Id;
        run.Created.Add((note.Id, page, content));

        var html = HtmlOutsideCode(content);
        if (html.Count > 0)
            run.Warnings.Add($"“{title}”: contains HTML ({string.Join(", ", html.Distinct().Select(t => $"<{t}>"))}) that may not display or survive editing.");
        if (content.Contains("$$"))
            run.Warnings.Add($"“{title}”: contains math formulas, which are shown as plain text.");
    }

    private async Task<(string Title, string Content)> ReadPageAsync(Run run, string page, CancellationToken ct)
    {
        string text;
        await using (var s = await run.Files[page].OpenAsync(ct))
        using (var reader = new StreamReader(s, Encoding.UTF8))
            text = (await reader.ReadToEndAsync(ct)).Replace("\r\n", "\n");

        // Docmost prepends "# Title"; use it as the title (it's unsanitised, unlike the file name).
        var title = SafeName(Path.GetFileNameWithoutExtension(page));
        var h1 = LeadingH1().Match(text);
        if (h1.Success)
        {
            var t = h1.Groups[1].Value.Trim();
            if (t.Length > 0) title = SafeName(t);
            text = text[h1.Length..];
        }

        var dir = ParentDir(page);
        text = ConvertYouTube(text);
        text = ConvertCallouts(text);
        text = ConvertDetails(text);
        text = HtmlImage().Replace(text, m => $"![{m.Groups["alt"].Value}]({m.Groups["src"].Value})");
        text = await ReplaceAsync(MarkdownImage(), text, m => ImportImageAsync(run, dir, m, title, ct));
        text = await ReplaceAsync(MarkdownLink(), text, m => RewriteAttachmentLinkAsync(run, dir, m, title, ct));
        return (title, text.Trim() + "\n");
    }

    private async Task<string> ImportImageAsync(Run run, string dir, Match m, string title, CancellationToken ct)
    {
        var alt = m.Groups["alt"].Value;
        var url = m.Groups["url"].Value;
        if (IsExternal(url)) return m.Value;

        var path = Resolve(dir, url);
        if (path is null || !run.Files.TryGetValue(path, out var entry))
        {
            run.Warnings.Add($"“{title}”: image not found in export ({Uri.UnescapeDataString(url)}).");
            return m.Value;
        }
        if (!run.ImageCache.TryGetValue(path, out var stored))
        {
            try
            {
                await using var s = await entry.OpenAsync(ct);
                stored = AttachmentStore.UrlPrefix + await attachments.SaveAsync(s, ct);
                run.ImageCache[path] = stored;
            }
            catch (DomainException ex)
            {
                run.Warnings.Add($"“{title}”: skipped {LastSegment(path)} — {ex.Message}");
                return alt;
            }
        }
        return $"![{alt}]({stored})";
    }

    // Links to attachment files: images are imported; other files (PDFs etc.) aren't supported yet.
    private async Task<string> RewriteAttachmentLinkAsync(Run run, string dir, Match m, string title, CancellationToken ct)
    {
        var url = m.Groups["url"].Value;
        if (IsExternal(url) || url.StartsWith('#')) return m.Value;
        var path = Resolve(dir, url);
        if (path is null || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !run.Files.ContainsKey(path))
            return m.Value; // internal page link (handled in pass 2) or unknown

        var text = m.Groups["text"].Value;
        await using (var s = await run.Files[path].OpenAsync(ct))
        {
            var head = new byte[16];
            var n = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
            if (AttachmentStore.SniffExtension(head.AsSpan(0, n)) is not null)
                return await ImportImageAsync(run, dir, MarkdownImage().Match($"![{text}]({url})"), title, ct);
        }
        run.Warnings.Add($"“{title}”: attachment “{LastSegment(path)}” wasn't imported (only images are supported).");
        return text;
    }

    // Pass 2: once every page has an id, point relative .md links at /notes/{id}.
    private async Task RewriteInternalLinksAsync(Run run, CancellationToken ct)
    {
        foreach (var (noteId, page, content) in run.Created)
        {
            var dir = ParentDir(page);
            var changed = false;
            var updated = MarkdownLink().Replace(content, m =>
            {
                var url = m.Groups["url"].Value;
                if (IsExternal(url)) return m.Value;
                var path = Resolve(dir, url);
                if (path is null || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return m.Value;
                if (!run.NoteByPagePath.TryGetValue(path, out var target)) return m.Groups["text"].Value;
                changed = true;
                return $"[{m.Groups["text"].Value}](/notes/{target})";
            });
            if (changed) await notes.SaveContentAsync(noteId, updated, ct);
        }
    }

    // Pass 3: keep each page's original created/updated dates from docmost-metadata.json, when present.
    private async Task ApplyOriginalDatesAsync(Run run, CancellationToken ct)
    {
        if (!run.Files.TryGetValue("docmost-metadata.json", out var entry)) return;
        try
        {
            await using var s = await entry.OpenAsync(ct);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            var dates = new Dictionary<string, (DateTimeOffset Created, DateTimeOffset Updated)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pages.EnumerateObject())
            {
                if (!p.Value.TryGetProperty("createdAt", out var c) || !c.TryGetDateTimeOffset(out var created)) continue;
                var updated = p.Value.TryGetProperty("updatedAt", out var u) && u.TryGetDateTimeOffset(out var up) ? up : created;
                dates[p.Name] = (created, updated);
                try { dates.TryAdd(Uri.UnescapeDataString(p.Name), (created, updated)); } catch (UriFormatException) { }
            }
            foreach (var (noteId, page, _) in run.Created)
                if (dates.TryGetValue(page, out var d)) await notes.SetTimestampsAsync(noteId, d.Created, d.Updated, ct);
        }
        catch (System.Text.Json.JsonException)
        {
            run.Warnings.Add("docmost-metadata.json couldn't be read, so original dates weren't kept.");
        }
    }

    // ---------- YouTube ----------

    /// <summary>Turns iframes / Docmost embed HTML / link-only lines pointing at YouTube into a bare URL line (our embed syntax).</summary>
    public static string ConvertYouTube(string markdown)
    {
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || !line.Contains("youtu", StringComparison.OrdinalIgnoreCase)) continue;
            var isHtml = line.StartsWith('<');
            var isLinkOnly = LinkOnlyLine().IsMatch(line);
            if (!isHtml && !isLinkOnly) continue;
            var id = YouTubeId().Match(line);
            if (id.Success) lines[i] = $"https://www.youtube.com/watch?v={id.Groups[1].Value}";
        }
        return string.Join('\n', lines);
    }

    // ---------- Docmost callouts & toggles ----------

    /// <summary>Docmost callouts (<c>:::info … :::</c>) become a quote with a labelled first line, which the editor can show and keep.</summary>
    public static string ConvertCallouts(string markdown) =>
        CalloutBlock().Replace(markdown, m =>
        {
            var type = m.Groups["type"].Value.ToLowerInvariant();
            var (emoji, label) = type switch
            {
                "info" => ("ℹ️", "Info"),
                "warning" => ("⚠️", "Warning"),
                "danger" or "error" => ("⛔", "Important"),
                "success" => ("✅", "Success"),
                "note" => ("📝", "Note"),
                _ => ("💡", char.ToUpperInvariant(type[0]) + type[1..]),
            };
            var lines = m.Groups["body"].Value.Trim('\n').Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l);
            return $"> **{emoji} {label}**\n>\n{string.Join('\n', lines)}";
        });

    /// <summary>Docmost toggles (<c>&lt;details&gt;</c>) become a bold title followed by their content (innermost first).</summary>
    public static string ConvertDetails(string markdown)
    {
        string previous;
        do
        {
            previous = markdown;
            markdown = DetailsBlock().Replace(markdown, m => $"**▸ {m.Groups["summary"].Value.Trim()}**\n\n{m.Groups["body"].Value.Trim()}\n");
        } while (markdown != previous);
        return markdown;
    }

    // ---------- helpers ----------

    private static async Task<string> ReplaceAsync(Regex regex, string input, Func<Match, Task<string>> replacer)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in regex.Matches(input))
        {
            sb.Append(input, last, m.Index - last).Append(await replacer(m));
            last = m.Index + m.Length;
        }
        return sb.Append(input, last, input.Length - last).ToString();
    }

    private static List<string> HtmlOutsideCode(string md)
    {
        var tags = new List<string>();
        var inFence = false;
        foreach (var line in md.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
            if (inFence) continue;
            foreach (Match m in HtmlTag().Matches(InlineCode().Replace(line, ""))) tags.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        return tags;
    }

    private static bool IsExternal(string url) =>
        url.Contains("://") || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || url.StartsWith('/');

    /// <summary>Resolves a relative, possibly URL-encoded link against a zip directory. Null if it escapes the root.</summary>
    public static string? Resolve(string dir, string url)
    {
        var clean = url.Split('#', '?')[0];
        if (clean.Length == 0) return null;
        string decoded;
        try { decoded = Uri.UnescapeDataString(clean); } catch { decoded = clean; }

        var parts = new List<string>(dir.Length == 0 ? [] : dir.Split('/'));
        foreach (var seg in decoded.Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static string NormalizeEntry(string name) => name.Replace('\\', '/').TrimStart('/');
    private static string ParentDir(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : "";
    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>The immediate subdirectory of <paramref name="dir"/> that contains <paramref name="page"/>, if any.</summary>
    private static string? ChildDirUnder(string dir, string page)
    {
        var prefix = dir.Length == 0 ? "" : dir + "/";
        if (!page.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = page[prefix.Length..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? null : prefix + rest[..slash];
    }

    private static string SafeName(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) n = "Untitled";
        return n.Length > Names.MaxLength ? n[..Names.MaxLength] : n;
    }

    [GeneratedRegex(@"(^|/)files/[0-9a-fA-F-]{8,}/", RegexOptions.IgnoreCase)] private static partial Regex AttachmentPath();
    [GeneratedRegex(@"^\s*#\s+(.*)\n*")] private static partial Regex LeadingH1();
    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)")] private static partial Regex MarkdownImage();
    [GeneratedRegex(@"(?<!!)\[(?<text>[^\]]*)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)")] private static partial Regex MarkdownLink();
    [GeneratedRegex(@"<img\b[^>]*?\bsrc=""(?<src>[^""]+)""[^>]*?(?:\balt=""(?<alt>[^""]*)"")?[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HtmlImage();
    [GeneratedRegex(@"^\[[^\]]*\]\([^)\s]+\)$")] private static partial Regex LinkOnlyLine();
    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:watch\?(?:[^""'\s)]*&)?v=|embed/|shorts/|live/|v/)|youtu\.be/)([\w-]{6,})", RegexOptions.IgnoreCase)] private static partial Regex YouTubeId();
    [GeneratedRegex(@"<(iframe|div|span|table|details|summary|video|audio|figure|p|br|u|mark|sub|sup)\b", RegexOptions.IgnoreCase)] private static partial Regex HtmlTag();
    [GeneratedRegex(@"`[^`]*`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"^:::(?<type>[A-Za-z]+)[ \t]*\n(?<body>[\s\S]*?)\n:::[ \t]*$", RegexOptions.Multiline)] private static partial Regex CalloutBlock();
    [GeneratedRegex(@"<details\b[^>]*>\s*<summary>(?<summary>(?:(?!</summary>|<details\b)[\s\S])*)</summary>(?<body>(?:(?!<details\b)[\s\S])*?)</details>", RegexOptions.IgnoreCase)] private static partial Regex DetailsBlock();
}
