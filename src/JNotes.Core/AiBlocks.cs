using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>A pending "/ai" request inside a note.</summary>
/// <param name="Section">Nearest heading above the request, for context.</param>
public sealed record AiRequest(string Id, string Prompt, string? Section);

/// <summary>
/// Markdown operations used by the MCP tools. The editor (editor.js) implements the same block
/// syntax and the same append / replace-section / fulfil semantics so it can patch an open note live.
/// <code>
/// :::ai-request{id="k3f9a2"}
/// Summarise this chapter
/// :::
///
/// :::ai-result{id="k3f9a2" prompt="Summarise this chapter"}
/// ...generated markdown...
/// :::
/// </code>
/// </summary>
public static partial class AiBlocks
{
    public const int MaxContentLength = 200_000;

    // ---------- requests ----------

    public static IReadOnlyList<AiRequest> FindRequests(string markdown)
    {
        var fences = FencedRanges(markdown);
        var result = new List<AiRequest>();
        foreach (Match m in RequestBlock().Matches(markdown))
        {
            if (InRanges(fences, m.Index)) continue;
            var attrs = ParseAttrs(m.Groups["attrs"].Value);
            if (!attrs.TryGetValue("id", out var id) || id.Length == 0) continue;
            var prompt = m.Groups["body"].Value.Trim();
            if (prompt.Length == 0) continue;
            result.Add(new AiRequest(id, prompt, HeadingAbove(markdown, m.Index, fences)));
        }
        return result;
    }

    /// <summary>Replaces the request block with an ai-result block. Returns (newMarkdown, resultBlock).</summary>
    public static (string Markdown, string Block) Fulfill(string markdown, string requestId, string content)
    {
        var fences = FencedRanges(markdown);
        foreach (Match m in RequestBlock().Matches(markdown))
        {
            if (InRanges(fences, m.Index)) continue;
            var attrs = ParseAttrs(m.Groups["attrs"].Value);
            if (attrs.GetValueOrDefault("id") != requestId) continue;

            var prompt = OneLine(m.Groups["body"].Value);
            var block = $":::ai-result{{id=\"{Escape(requestId)}\" prompt=\"{Escape(prompt)}\"}}\n{SanitizeBody(content)}\n:::";
            var trailing = m.Value.EndsWith('\n') ? "\n" : "";
            return (markdown[..m.Index] + block + trailing + markdown[(m.Index + m.Length)..], block);
        }
        throw new DomainException($"No pending AI request with id “{requestId}” in this note. Use list_pending_requests to see open requests.");
    }

    /// <summary>Replaces one request block with a plain marker, so a model can see exactly where its answer goes.</summary>
    public static string MarkRequest(string markdown, string requestId, string marker)
    {
        var fences = FencedRanges(markdown);
        foreach (Match m in RequestBlock().Matches(markdown))
        {
            if (InRanges(fences, m.Index) || ParseAttrs(m.Groups["attrs"].Value).GetValueOrDefault("id") != requestId) continue;
            var trailing = m.Value.EndsWith('\n') ? "\n" : "";
            return markdown[..m.Index] + marker + trailing + markdown[(m.Index + m.Length)..];
        }
        return markdown;
    }

    // ---------- append / sections ----------

    public static string Append(string markdown, string content)
    {
        var body = SanitizeBody(content);
        var existing = markdown.TrimEnd();
        return existing.Length == 0 ? body + "\n" : existing + "\n\n" + body + "\n";
    }

    public sealed record Heading(int Level, string Text, int LineIndex);

    public static IReadOnlyList<Heading> Headings(string markdown)
    {
        var lines = markdown.Split('\n');
        var result = new List<Heading>();
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsFence(lines[i])) { inFence = !inFence; continue; }
            if (inFence) continue;
            var m = HeadingLine().Match(lines[i]);
            if (m.Success) result.Add(new Heading(m.Groups[1].Length, m.Groups[2].Value.Trim(), i));
        }
        return result;
    }

    /// <summary>Replaces everything under <paramref name="heading"/> up to the next heading of the same or higher level.</summary>
    public static string ReplaceSection(string markdown, string heading, string content)
    {
        var headings = Headings(markdown);
        var target = headings.FirstOrDefault(h => SameHeading(h.Text, heading))
                     ?? throw new DomainException(headings.Count == 0
                         ? $"Heading “{heading}” not found — the note has no headings. Use append_to_note instead."
                         : $"Heading “{heading}” not found. Headings in this note: {string.Join(", ", headings.Select(h => $"“{h.Text}”"))}.");

        var end = headings.FirstOrDefault(h => h.LineIndex > target.LineIndex && h.Level <= target.Level);
        var lines = markdown.Split('\n').ToList();
        var endLine = end?.LineIndex ?? lines.Count;

        var replacement = new List<string> { "" };
        replacement.AddRange(SanitizeBody(content).Split('\n'));
        replacement.Add("");
        lines.RemoveRange(target.LineIndex + 1, endLine - target.LineIndex - 1);
        lines.InsertRange(target.LineIndex + 1, end is null ? replacement.SkipLast(1) : replacement);
        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    public static bool SameHeading(string a, string b) =>
        string.Equals(NormalizeHeading(a), NormalizeHeading(b), StringComparison.CurrentCultureIgnoreCase);

    private static string NormalizeHeading(string s) => Whitespace().Replace(s.Trim().TrimStart('#').Trim().TrimEnd('#').Trim(), " ");

    // ---------- helpers ----------

    /// <summary>Validates generated markdown and neutralises anything that could close or forge an AI block.</summary>
    public static string SanitizeBody(string content)
    {
        var text = (content ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) throw new DomainException("Content can't be empty.");
        if (text.Length > MaxContentLength) throw new DomainException($"Content is too long (max {MaxContentLength:N0} characters).");
        return DirectiveLine().Replace(text, @"\:::");
    }

    private static string OneLine(string s) => Whitespace().Replace(s.Trim(), " ");

    /// <summary>Attribute escaping shared with editor.js: only &amp; and " are encoded.</summary>
    public static string Escape(string s) => s.Replace('\n', ' ').Replace("&", "&amp;").Replace("\"", "&quot;");

    private static string Unescape(string s) => s.Replace("&quot;", "\"").Replace("&amp;", "&");

    private static Dictionary<string, string> ParseAttrs(string s)
    {
        var attrs = new Dictionary<string, string>();
        foreach (Match m in AttrPair().Matches(s)) attrs.TryAdd(m.Groups[1].Value, Unescape(m.Groups[2].Value));
        return attrs;
    }

    private static string? HeadingAbove(string markdown, int index, List<(int Start, int End)> fences)
    {
        string? found = null;
        foreach (Match m in HeadingLineMultiline().Matches(markdown[..index]))
            if (!InRanges(fences, m.Index)) found = m.Groups[2].Value.Trim();
        return found;
    }

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~");

    private static List<(int Start, int End)> FencedRanges(string markdown)
    {
        var ranges = new List<(int, int)>();
        int? open = null;
        var pos = 0;
        foreach (var line in markdown.Split('\n'))
        {
            if (IsFence(line))
            {
                if (open is null) open = pos;
                else { ranges.Add((open.Value, pos + line.Length)); open = null; }
            }
            pos += line.Length + 1;
        }
        if (open is not null) ranges.Add((open.Value, markdown.Length));
        return ranges;
    }

    private static bool InRanges(List<(int Start, int End)> ranges, int index) => ranges.Any(r => index >= r.Start && index <= r.End);

    [GeneratedRegex(@"^:::ai-request\{(?<attrs>[^}\n]*)\}[ \t]*\n(?<body>[\s\S]*?)\n:::[ \t]*(?:\n|$)", RegexOptions.Multiline)]
    private static partial Regex RequestBlock();
    [GeneratedRegex(@"(\w+)=""([^""]*)""")] private static partial Regex AttrPair();
    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")] private static partial Regex HeadingLine();
    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Multiline)] private static partial Regex HeadingLineMultiline();
    [GeneratedRegex(@"^:::", RegexOptions.Multiline)] private static partial Regex DirectiveLine();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
