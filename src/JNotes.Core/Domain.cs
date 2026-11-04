namespace JNotes.Core;

public sealed class Folder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public Guid? FolderId { get; set; }

    /// <summary>Markdown — the source of truth for the document body.</summary>
    public string Content { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Lightweight item for building the sidebar tree.</summary>
public sealed record TreeEntry(Guid Id, string Name, Guid? ParentId, bool IsFolder);

public sealed class DomainException(string message) : Exception(message);

public static class Names
{
    public const int MaxLength = 200;

    public static string Normalize(string? name, string what)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new DomainException($"{what} name can't be empty.");
        if (trimmed.Length > MaxLength) throw new DomainException($"{what} name is too long (max {MaxLength}).");
        return trimmed;
    }
}
