using System.Text.RegularExpressions;

namespace JNotes.Core;

public interface ITemplateStore
{
    Task<IReadOnlyList<Template>> ListAsync(CancellationToken ct = default);
    Task<Template?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Template> CreateAsync(string name, string content = "", CancellationToken ct = default);
    Task RenameAsync(Guid id, string name, CancellationToken ct = default);
    Task SaveContentAsync(Guid id, string content, CancellationToken ct = default);
    /// <summary>Deleting a template clears it as any folder's default.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public static partial class TemplateRenderer
{
    public static readonly IReadOnlyList<(string Name, string Description)> Variables =
    [
        ("{{title}}", "the new note's title"),
        ("{{date}}", "today's date (yyyy-MM-dd)"),
        ("{{folder}}", "the folder the note is created in"),
    ];

    /// <summary>Replaces known variables (case-insensitive, whitespace-tolerant); unknown ones are left as-is.</summary>
    public static string Render(string template, string title, string? folderName, DateTimeOffset now) =>
        VariablePattern().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "title" => title,
            "date" => now.ToString("yyyy-MM-dd"),
            "folder" => folderName ?? "",
            _ => m.Value,
        });

    [GeneratedRegex(@"\{\{\s*([A-Za-z]+)\s*\}\}")]
    private static partial Regex VariablePattern();
}

/// <summary>Creates notes, optionally from a template. Shared by the UI and (later) MCP tools.</summary>
public sealed class NoteFactory(INoteStore notes, ITemplateStore templates, TimeProvider clock)
{
    public async Task<Note> CreateAsync(string title, Guid? folderId, Guid? templateId, CancellationToken ct = default)
    {
        var content = "";
        if (templateId is { } tid)
        {
            var template = await templates.GetAsync(tid, ct) ?? throw new DomainException("Template not found.");
            var folderName = folderId is { } fid ? (await notes.GetFolderAsync(fid, ct))?.Name : null;
            content = TemplateRenderer.Render(template.Content, title.Trim(), folderName, clock.GetLocalNow());
        }
        return await notes.CreateNoteAsync(title, folderId, content, ct);
    }
}
