using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>A page row exported from Docmost's Postgres database.</summary>
public sealed record DocmostPage(
    Guid Id, string? SlugId, string? Title, Guid? ParentPageId, string Space,
    JsonElement? Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>An attachment row; <see cref="FilePath"/> is relative to Docmost's storage directory.</summary>
public sealed record DocmostAttachment(Guid Id, string FileName, string FilePath);

/// <summary>
/// Turns a Docmost database dump into the same zip layout as Docmost's own Markdown export, which
/// <see cref="DocmostImporter"/> already handles: one folder per space, <c>Page.md</c> (+ <c>Page/</c> for children),
/// images under <c>files/&lt;id&gt;/</c>, page links as relative <c>.md</c> paths, and docmost-metadata.json with dates.
/// </summary>
public static partial class DocmostDbConverter
{
    public sealed record Result(int Pages, int Images, IReadOnlyList<string> Warnings);

    public static async Task<Result> WriteZipAsync(
        IReadOnlyList<DocmostPage> pages, IReadOnlyList<DocmostAttachment> attachments,
        Func<string, Stream?> openStorageFile, Stream output, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var paths = AssignPaths(pages);
        var bySlug = pages.Where(p => !string.IsNullOrEmpty(p.SlugId)).GroupBy(p => p.SlugId!).ToDictionary(g => g.Key, g => g.First().Id);
        var byTitle = pages.GroupBy(p => (p.Title ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Id, StringComparer.OrdinalIgnoreCase);
        var attachmentsById = attachments.ToDictionary(a => a.Id);
        var images = new Dictionary<Guid, string>(); // attachment id -> zip path
        var metadata = new Dictionary<string, PageMeta>();

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        foreach (var page in pages)
        {
            var path = paths[page.Id];
            var title = Title(page);

            string? MapImage(string src)
            {
                var m = DocmostFileUrl().Match(src);
                if (!m.Success || !Guid.TryParse(m.Groups["id"].Value, out var id) || !attachmentsById.TryGetValue(id, out var a))
                {
                    warnings.Add($"“{title}”: an image is missing (it was already broken in Docmost).");
                    return null;
                }
                if (!images.ContainsKey(id)) images[id] = $"files/{id}/{a.FileName}";
                return Exporter.EncodePath(Exporter.RelativePath(path, images[id]));
            }

            string MapLink(string href)
            {
                Guid? target = null;
                if (DocmostPageUrl().Match(href) is { Success: true } p && bySlug.TryGetValue(p.Groups["slug"].Value, out var bySlugId)) target = bySlugId;
                else if (NotionMdLink().Match(href) is { Success: true } n && byTitle.TryGetValue(Decode(n.Groups["title"].Value), out var byTitleId)) target = byTitleId;
                return target is { } t && paths.TryGetValue(t, out var targetPath)
                    ? Exporter.EncodePath(Exporter.RelativePath(path, targetPath))
                    : href;
            }

            var body = page.Content is { ValueKind: JsonValueKind.Object } doc
                ? new ProseMirrorMarkdown(MapImage, MapLink).Convert(doc)
                : "";
            await WriteAsync(zip, path, Encoding.UTF8.GetBytes($"# {title}\n\n{body}"), ct);
            metadata[path] = new PageMeta(page.CreatedAt, page.UpdatedAt);
        }

        foreach (var (id, zipPath) in images)
        {
            await using var file = openStorageFile(attachmentsById[id].FilePath);
            if (file is null) { warnings.Add($"Image file “{attachmentsById[id].FileName}” wasn't found in Docmost's storage."); continue; }
            var entry = zip.CreateEntry(zipPath, CompressionLevel.NoCompression);
            await using var s = await entry.OpenAsync(ct);
            await file.CopyToAsync(s, ct);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new { source = "jnotes-docmost-db", pages = metadata }, JsonOptions);
        await WriteAsync(zip, "docmost-metadata.json", json, ct);
        return new Result(pages.Count, images.Count, warnings);
    }

    /// <summary>"Space/Title.md", with children under "Space/Title/…"; sibling names are made unique.</summary>
    private static Dictionary<Guid, string> AssignPaths(IReadOnlyList<DocmostPage> pages)
    {
        var result = new Dictionary<Guid, string>();
        var children = pages.ToLookup(p => p.ParentPageId);
        var ids = pages.Select(p => p.Id).ToHashSet();

        void Walk(IEnumerable<DocmostPage> level, string dir)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in level.OrderBy(p => Title(p), StringComparer.CurrentCultureIgnoreCase))
            {
                var baseName = Exporter.SafeFileName(Title(page));
                var name = baseName;
                for (var i = 2; !taken.Add(name); i++) name = $"{baseName} ({i})";
                result[page.Id] = $"{dir}{name}.md";
                Walk(children[page.Id], $"{dir}{name}/");
            }
        }

        var spaceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var space in pages.GroupBy(p => p.Space).OrderBy(g => g.Key))
        {
            var spaceDir = Exporter.SafeFileName(space.Key);
            for (var i = 2; !spaceNames.Add(spaceDir); i++) spaceDir = $"{Exporter.SafeFileName(space.Key)} ({i})";
            // Roots: no parent, or a parent that isn't in the dump (e.g. deleted).
            Walk(space.Where(p => p.ParentPageId is null || !ids.Contains(p.ParentPageId.Value)), spaceDir + "/");
        }
        return result;
    }

    private static string Title(DocmostPage p) => string.IsNullOrWhiteSpace(p.Title) ? "Untitled" : p.Title.Trim();

    private static string Decode(string s)
    {
        try { s = Uri.UnescapeDataString(s); } catch (UriFormatException) { }
        return NotionHash().Replace(s, "").Trim();
    }

    private static async Task WriteAsync(ZipArchive zip, string path, byte[] data, CancellationToken ct)
    {
        await using var s = await zip.CreateEntry(path).OpenAsync(ct);
        await s.WriteAsync(data, ct);
    }

    public sealed record PageMeta(DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [GeneratedRegex(@"^(?:https?://[^/]+)?/(?:api/)?files/(?<id>[0-9a-fA-F-]{36})/")] private static partial Regex DocmostFileUrl();
    [GeneratedRegex(@"/p/(?:[^/?#]*-)?(?<slug>[A-Za-z0-9]{6,})/?(?:[?#].*)?$")] private static partial Regex DocmostPageUrl();
    [GeneratedRegex(@"^(?!https?:)(?:\.\./|\./)*(?:[^/]*/)*(?<title>[^/]+)\.md(?:#.*)?$")] private static partial Regex NotionMdLink();
    [GeneratedRegex(@"\s+[0-9a-f]{32}$")] private static partial Regex NotionHash();
}
