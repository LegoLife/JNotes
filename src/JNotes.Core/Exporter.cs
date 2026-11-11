using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>
/// Exports notes as portable markdown. The vault zip mirrors the folder tree (one <c>Title.md</c> per note,
/// starting with <c># Title</c>), with referenced images under <c>attachments/</c> and links between notes
/// rewritten to relative <c>.md</c> paths — the same shape the Docmost importer reads, so exports re-import cleanly.
/// </summary>
public sealed partial class Exporter(INoteStore notes, AttachmentStore attachments)
{
    public sealed record ExportFile(string FileName, string ContentType, byte[] Data);

    /// <summary>Writes the whole vault (excluding Trash) as a zip to <paramref name="output"/>.</summary>
    public async Task WriteVaultAsync(Stream output, CancellationToken ct = default)
    {
        var index = new NoteTreeIndex(await notes.GetTreeAsync(ct));
        var paths = AssignPaths(index);
        var usedImages = new HashSet<string>(StringComparer.Ordinal);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        foreach (var (noteId, path) in paths)
        {
            var note = await notes.GetNoteAsync(noteId, ct);
            if (note is null) continue;
            var depth = path.Count(c => c == '/');
            var body = RewriteImages(note.Content, string.Concat(Enumerable.Repeat("../", depth)) + "attachments/", usedImages);
            body = InternalLink().Replace(body, m =>
                Guid.TryParse(m.Groups["id"].Value, out var target) && paths.TryGetValue(target, out var targetPath)
                    ? $"]({EncodePath(RelativePath(path, targetPath))})"
                    : m.Value);
            await WriteEntryAsync(zip, path, Encoding.UTF8.GetBytes(Document(note.Title, body)), ct);
        }
        await WriteAttachmentsAsync(zip, usedImages, ct);
    }

    /// <summary>One note as <c>Title.md</c>, or as <c>Title.zip</c> (note + images) if it contains images.</summary>
    public async Task<ExportFile> ExportNoteAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await notes.GetNoteAsync(noteId, ct) ?? throw new DomainException("Note not found.");
        var name = SafeFileName(note.Title);
        var images = new HashSet<string>(StringComparer.Ordinal);
        var body = RewriteImages(note.Content, "attachments/", images);
        var markdown = Encoding.UTF8.GetBytes(Document(note.Title, body));
        if (images.Count == 0) return new ExportFile(name + ".md", "text/markdown; charset=utf-8", markdown);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            await WriteEntryAsync(zip, name + ".md", markdown, ct);
            await WriteAttachmentsAsync(zip, images, ct);
        }
        return new ExportFile(name + ".zip", "application/zip", buffer.ToArray());
    }

    // ---------- helpers ----------

    // Names are either "{guid}.{ext}" (images) or "{guid}/{file name}" (other files).
    private async Task WriteAttachmentsAsync(ZipArchive zip, IEnumerable<string> names, CancellationToken ct)
    {
        foreach (var name in names)
        {
            var slash = name.IndexOf('/');
            var file = slash < 0 ? attachments.GetPath(name) : attachments.GetFilePath(name[..slash], name[(slash + 1)..]);
            if (file is null) continue;
            var entry = zip.CreateEntry("attachments/" + name, CompressionLevel.NoCompression);
            await using var target = await entry.OpenAsync(ct);
            await using var source = File.OpenRead(file);
            await source.CopyToAsync(target, ct);
        }
    }

    private static string Document(string title, string body) => $"# {title}\n\n{body.TrimEnd()}\n";

    /// <summary>Gives every live note a unique path like "Books/Dune/Chapter 1.md" (duplicates get " (2)").</summary>
    public static Dictionary<Guid, string> AssignPaths(NoteTreeIndex index)
    {
        var result = new Dictionary<Guid, string>();
        void Walk(Guid? folderId, string prefix)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in index.ChildrenOf(folderId))
            {
                var baseName = SafeFileName(child.Name);
                var name = baseName;
                var ext = child.IsFolder ? "" : ".md";
                for (var i = 2; !taken.Add(name + ext); i++) name = $"{baseName} ({i})";
                if (child.IsFolder) Walk(child.Id, prefix + name + "/");
                else result[child.Id] = prefix + name + ".md";
            }
        }
        Walk(null, "");
        return result;
    }

    public static string SafeFileName(string name)
    {
        var cleaned = InvalidFileChars().Replace(name, "-").Trim().Trim('.').Trim();
        if (cleaned.Length > 120) cleaned = cleaned[..120].Trim();
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    private static string RewriteImages(string markdown, string prefix, HashSet<string> used)
    {
        markdown = AttachmentLink().Replace(markdown, m =>
        {
            used.Add(m.Groups["name"].Value);
            return $"]({prefix}{m.Groups["name"].Value}";
        });
        return FileLink().Replace(markdown, m =>
        {
            var name = Uri.UnescapeDataString(m.Groups["name"].Value);
            if (name != AttachmentStore.SafeFileName(name)) return m.Value;
            used.Add($"{m.Groups["id"].Value}/{name}");
            return $"]({prefix}{m.Groups["id"].Value}/{m.Groups["name"].Value})";
        });
    }

    public static string RelativePath(string fromFile, string toFile)
    {
        var from = fromFile.Split('/')[..^1];
        var to = toFile.Split('/');
        var common = 0;
        while (common < from.Length && common < to.Length - 1 && from[common] == to[common]) common++;
        return string.Concat(Enumerable.Repeat("../", from.Length - common)) + string.Join('/', to[common..]);
    }

    public static string EncodePath(string path) => string.Join('/', path.Split('/').Select(s => s == ".." ? s : Uri.EscapeDataString(s)));

    private static async Task WriteEntryAsync(ZipArchive zip, string path, byte[] data, CancellationToken ct, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(path, level);
        await using var s = await entry.OpenAsync(ct);
        await s.WriteAsync(data, ct);
    }

    [GeneratedRegex(@"\]\(/attachments/(?<name>[0-9a-f]{32}\.(?:png|jpg|gif|webp))")] private static partial Regex AttachmentLink();
    [GeneratedRegex(@"\]\(/attachments/(?<id>[0-9a-f]{32})/(?<name>[^/)\s]+)\)")] private static partial Regex FileLink();
    [GeneratedRegex(@"\]\(/notes/(?<id>[0-9a-fA-F-]{36})\)")] private static partial Regex InternalLink();
    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]")] private static partial Regex InvalidFileChars();
}
