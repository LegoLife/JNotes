using System.Text;
using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>A search result. <see cref="Snippet"/> marks matches with <see cref="Search.HighlightStart"/>/<see cref="Search.HighlightEnd"/>.</summary>
public sealed record SearchHit(Guid NoteId, string Title, Guid? FolderId, string Snippet, DateTimeOffset UpdatedAt);

public interface INoteSearch
{
    /// <param name="folderId">Restrict to this folder and its subfolders.</param>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, Guid? folderId = null, int limit = 50, CancellationToken ct = default);
}

public static partial class Search
{
    public const char HighlightStart = '\u0002';
    public const char HighlightEnd = '\u0003';

    /// <summary>
    /// Turns user input into a safe FTS5 query: every word must match (as a prefix);
    /// "quoted phrases" must match exactly. Returns null when there is nothing to search for.
    /// </summary>
    public static string? BuildFtsQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = new List<string>();

        // Quoted phrases first, then whatever words remain.
        var rest = PhrasePattern().Replace(input, m =>
        {
            var words = Words(m.Groups[1].Value);
            if (words.Count > 0) parts.Add("\"" + string.Join(' ', words) + "\"");
            return " ";
        });
        parts.AddRange(Words(rest).Select(w => "\"" + w + "\"*"));

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    // FTS5's unicode61 tokenizer splits on anything that isn't a letter or digit, so do the same.
    private static List<string> Words(string s) =>
        WordPattern().Matches(s).Select(m => m.Value).ToList();

    /// <summary>Strips the noisier markdown syntax from a snippet for display, keeping highlight markers.</summary>
    public static string CleanSnippet(string snippet)
    {
        var s = MdImage().Replace(snippet, "");
        s = MdLink().Replace(s, "$1");
        s = MdTableRule().Replace(s, " ");
        s = MdTaskBox().Replace(s, " ");
        s = s.Replace('|', ' ');
        s = MdSyntax().Replace(s, "");
        return Whitespace().Replace(s, " ").Trim();
    }

    /// <summary>Splits a snippet into (text, isHighlighted) runs for safe rendering.</summary>
    public static IEnumerable<(string Text, bool Highlighted)> SplitHighlights(string snippet)
    {
        var sb = new StringBuilder();
        var on = false;
        foreach (var c in snippet)
        {
            if (c is HighlightStart or HighlightEnd)
            {
                if (sb.Length > 0) yield return (sb.ToString(), on);
                sb.Clear();
                on = c == HighlightStart;
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return (sb.ToString(), on);
    }

    [GeneratedRegex("\"([^\"]*)\"?")] private static partial Regex PhrasePattern();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex WordPattern();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex MdImage();
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")] private static partial Regex MdLink();
    [GeneratedRegex(@"(^|\s)#{1,6}\s|[*_`>]+")] private static partial Regex MdSyntax();
    [GeneratedRegex(@"\|?(\s*:?-{3,}:?\s*\|)+\s*(:?-{3,}:?)?")] private static partial Regex MdTableRule();
    [GeneratedRegex(@"(^|\s)[-*+]?\s*\[[ xX]\]\s")] private static partial Regex MdTaskBox();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
