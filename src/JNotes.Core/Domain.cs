namespace JNotes.Core;

public sealed class Folder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>Template pre-selected when creating a note in this folder.</summary>
    public Guid? DefaultTemplateId { get; set; }

    /// <summary>Position among its siblings (folders and notes share one order). 0 = not custom-ordered.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when moved to Trash. Items trashed together share a <see cref="TrashBatch"/>.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? TrashBatch { get; set; }
}

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public Guid? FolderId { get; set; }

    /// <summary>Position among its siblings (folders and notes share one order). 0 = not custom-ordered.</summary>
    public int SortOrder { get; set; }

    /// <summary>Markdown — the source of truth for the document body.</summary>
    public string Content { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when moved to Trash. Items trashed together share a <see cref="TrashBatch"/>.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? TrashBatch { get; set; }
}

/// <summary>A saved earlier state of a note.</summary>
public sealed class NoteVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NoteId { get; set; }
    public required string Title { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Why it was saved, e.g. "Before Claude: fill request", "While editing", "Before restore".</summary>
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A reusable markdown skeleton. Supports {{title}}, {{date}} and {{folder}}.</summary>
public sealed class Template
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
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
