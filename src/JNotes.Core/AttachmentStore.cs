using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>
/// Stores uploaded images as files named {guid}.{ext} (type sniffed from the bytes, never trusted from the client),
/// and any other file as {guid}/{original name}, addressed as <c>/attachments/{guid}/{name}</c>.
/// </summary>
public sealed partial class AttachmentStore
{
    public const long MaxBytes = 20 * 1024 * 1024;
    public const long MaxFileBytes = 200 * 1024 * 1024;
    public const string UrlPrefix = "/attachments/";

    private readonly string _directory;

    public AttachmentStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Saves an image and returns its file name. Throws <see cref="DomainException"/> for non-images or oversize files.</summary>
    public async Task<string> SaveAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new DomainException($"Images must be under {MaxBytes / 1024 / 1024} MB.");
            buffer.Write(chunk, 0, read);
        }

        var ext = SniffExtension(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))
                  ?? throw new DomainException("Only PNG, JPEG, GIF and WebP images are supported.");
        var name = $"{Guid.NewGuid():N}.{ext}";
        var path = Path.Combine(_directory, name);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, buffer.ToArray(), ct);
        File.Move(temp, path);
        return name;
    }

    /// <summary>Saves any file under its (sanitised) original name and returns its relative name "{guid}/{name}".</summary>
    public async Task<string> SaveFileAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var safe = SafeFileName(fileName);
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(_directory, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, safe);
        var temp = Path.Combine(dir, ".upload.tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(chunk, ct)) > 0)
                {
                    total += read;
                    if (total > MaxFileBytes) throw new DomainException($"Files must be under {MaxFileBytes / 1024 / 1024} MB.");
                    await fs.WriteAsync(chunk.AsMemory(0, read), ct);
                }
            }
            File.Move(temp, path);
            return $"{id}/{safe}";
        }
        catch
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Full path for a stored attachment, or null if the name is invalid or missing.</summary>
    public string? GetPath(string name)
    {
        if (!IsValidName(name)) return null;
        var path = Path.Combine(_directory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Full path for a stored file attachment ("{guid}/{name}"), or null if invalid or missing.</summary>
    public string? GetFilePath(string id, string name)
    {
        if (!FileIdPattern().IsMatch(id) || name != SafeFileName(name)) return null;
        var path = Path.Combine(_directory, id, name);
        return File.Exists(path) ? path : null;
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>A file name that is safe on disk and in a URL segment: no path parts, control or reserved characters.</summary>
    public static string SafeFileName(string name)
    {
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var cleaned = UnsafeFileChars().Replace(name, "_").Trim().TrimEnd('.').Trim();
        if (cleaned.Length > 150)
        {
            var ext = Path.GetExtension(cleaned);
            if (ext.Length > 20) ext = "";
            cleaned = cleaned[..(150 - ext.Length)].TrimEnd() + ext;
        }
        return cleaned.Length == 0 || cleaned.StartsWith('.') ? "file" + cleaned : cleaned;
    }

    public static string ContentTypeFor(string name) => Path.GetExtension(name) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>Content type for file attachments that are safe to show in the browser; null = download only.</summary>
    public static string? InlineContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".txt" or ".log" or ".md" or ".csv" or ".json" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        _ => null,
    };

    public static string? SniffExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return "gif";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "webp";
        return null;
    }

    [GeneratedRegex(@"^[0-9a-f]{32}\.(png|jpg|gif|webp)$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9a-f]{32}$")]
    private static partial Regex FileIdPattern();

    [GeneratedRegex(@"[\x00-\x1f\x7f<>:""/\\|?*%#]")]
    private static partial Regex UnsafeFileChars();
}
