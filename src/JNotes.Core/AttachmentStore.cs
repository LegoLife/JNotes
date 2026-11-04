using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>Stores uploaded images as files named {guid}.{ext}. The type is sniffed from the bytes, never trusted from the client.</summary>
public sealed partial class AttachmentStore
{
    public const long MaxBytes = 20 * 1024 * 1024;
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

    /// <summary>Full path for a stored attachment, or null if the name is invalid or missing.</summary>
    public string? GetPath(string name)
    {
        if (!IsValidName(name)) return null;
        var path = Path.Combine(_directory, name);
        return File.Exists(path) ? path : null;
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static string ContentTypeFor(string name) => Path.GetExtension(name) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
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
}
