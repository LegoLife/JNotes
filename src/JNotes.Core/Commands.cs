using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>A reusable instruction for Claude, exposed as an MCP prompt (shows up in Claude's "+" menu).</summary>
public sealed class Command
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>MCP prompt name (slug), e.g. "chapter-summary".</summary>
    public required string Name { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Instruction text; <c>{{argument}}</c> placeholders become prompt arguments.</summary>
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public interface ICommandStore
{
    Task<IReadOnlyList<Command>> ListAsync(CancellationToken ct = default);
    Task<Command?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Command?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<Command> CreateAsync(string title, string description, string body, CancellationToken ct = default);
    Task UpdateAsync(Guid id, string title, string description, string body, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public static partial class CommandPrompts
{
    /// <summary>Appended to every command so Claude stays grounded in the user's own notes.</summary>
    public const string GroundRules = """

        Ground rules:
        - Base what you write on my notes first (use search_notes / read_note). Add your own knowledge only where you're confident, and clearly flag anything you're unsure about.
        - Write into JNotes with the tools (fulfill_request, replace_section, append_to_note, create_note) rather than only replying in chat. Never delete or overwrite whole notes.
        - Write clean markdown. Don't include the note's title as a heading, and don't write :::ai blocks yourself.
        - When you're done, tell me briefly which notes you changed.
        """;

    /// <summary>Argument names used in the body, in order of first appearance.</summary>
    public static IReadOnlyList<string> Arguments(string body) =>
        Placeholder().Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

    public static string Render(string body, IReadOnlyDictionary<string, string>? args) =>
        Placeholder().Replace(body, m =>
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            return args is not null && args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : $"(no {name} given)";
        }) + "\n" + GroundRules;

    public static string Slugify(string title)
    {
        var slug = NonSlug().Replace(title.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "command" : slug;
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}")] private static partial Regex Placeholder();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NonSlug();
}
