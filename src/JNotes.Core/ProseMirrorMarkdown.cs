using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JNotes.Core;

/// <summary>
/// Converts a Docmost (Tiptap/ProseMirror) JSON document to markdown in JNotes' dialect.
/// Covers Docmost's block and mark types; anything unknown falls back to its text so nothing is silently lost.
/// </summary>
public sealed partial class ProseMirrorMarkdown(Func<string, string?> mapImage, Func<string, string> mapLink)
{
    /// <param name="mapImage">Maps an image src to a usable URL, or null if the image is missing.</param>
    /// <param name="mapLink">Maps a link href (e.g. to another page) to its new target.</param>
    public string Convert(JsonElement doc) =>
        Blocks(Children(doc)).Trim() is { Length: > 0 } md ? md + "\n" : "";

    private string Blocks(IEnumerable<JsonElement> nodes) =>
        string.Join("\n\n", nodes.Select(Block).Where(b => !string.IsNullOrWhiteSpace(b)));

    private string? Block(JsonElement node) => Type(node) switch
    {
        "paragraph" => EscapeLineStart(Inline(Children(node))),
        "heading" => $"{new string('#', Math.Clamp(Attr(node, "level")?.GetInt32() ?? 1, 1, 6))} {Inline(Children(node))}",
        "bulletList" => List(node, _ => "- "),
        "orderedList" => List(node, i => $"{(Attr(node, "start")?.GetInt32() ?? 1) + i}. "),
        "taskList" => List(node, _ => "- "),
        "blockquote" => Quote(Blocks(Children(node))),
        "callout" => Quote($"**{CalloutLabel(AttrString(node, "type"))}**\n\n{Blocks(Children(node))}"),
        "codeBlock" => $"```{AttrString(node, "language")}\n{PlainText(node)}\n```",
        "horizontalRule" => "---",
        "table" => Table(node),
        "image" => Image(node),
        "embed" or "youtube" or "video" => Embed(node),
        "details" => Details(node),
        "mathBlock" => $"$$\n{PlainText(node)}\n$$",
        _ => Children(node).Any(c => IsBlock(c)) ? Blocks(Children(node)) : Inline(Children(node)),
    };

    // ---------- lists ----------

    private string List(JsonElement list, Func<int, string> marker)
    {
        var lines = new List<string>();
        var i = 0;
        foreach (var item in Children(list))
        {
            var prefix = marker(i++);
            if (Type(item) == "taskItem") prefix += Attr(item, "checked")?.ValueKind == JsonValueKind.True ? "[x] " : "[ ] ";
            // Tight list: an item's paragraph and nested list sit on consecutive lines.
            var body = string.Join('\n', Children(item).Select(Block).Where(b => !string.IsNullOrWhiteSpace(b)));
            var bodyLines = body.Length == 0 ? [""] : body.Split('\n');
            var pad = new string(' ', marker(i - 1).Length);
            lines.Add(prefix + bodyLines[0]);
            lines.AddRange(bodyLines.Skip(1).Select(l => l.Length == 0 ? "" : pad + l));
        }
        return string.Join('\n', lines);
    }

    // ---------- other blocks ----------

    private static string Quote(string body) =>
        string.Join('\n', body.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

    private static string CalloutLabel(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "warning" => "⚠️ Warning",
        "danger" or "error" => "⛔ Important",
        "success" => "✅ Success",
        "note" => "📝 Note",
        "info" => "ℹ️ Info",
        _ => "💡 Note",
    };

    private string Table(JsonElement table)
    {
        var rows = Children(table).Select(r => Children(r).Select(c => Cell(c)).ToList()).Where(r => r.Count > 0).ToList();
        if (rows.Count == 0) return "";
        var width = rows.Max(r => r.Count);
        foreach (var r in rows) while (r.Count < width) r.Add("");
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", rows[0])).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).Append(" |");
        foreach (var r in rows.Skip(1)) sb.Append("\n| ").Append(string.Join(" | ", r)).Append(" |");
        return sb.ToString();
    }

    private string Cell(JsonElement cell) =>
        string.Join(" ", Children(cell).Select(b => Inline(Children(b), inTable: true)).Where(s => s.Length > 0)).Replace("|", "\\|");

    private string Image(JsonElement node)
    {
        var src = AttrString(node, "src") ?? "";
        var alt = (AttrString(node, "alt") ?? "").Replace("]", "");
        return mapImage(src) is { } url ? $"![{alt}]({url})" : $"*(missing image{(alt.Length > 0 && alt != "Untitled" ? $": {alt}" : "")})*";
    }

    private static string Embed(JsonElement node)
    {
        var src = AttrString(node, "src") ?? "";
        return src.Contains("youtu", StringComparison.OrdinalIgnoreCase) ? src : $"[{src}]({src})";
    }

    private string Details(JsonElement node)
    {
        var summary = Children(node).FirstOrDefault(c => Type(c) == "detailsSummary");
        var content = Children(node).Where(c => Type(c) != "detailsSummary");
        var title = summary.ValueKind == JsonValueKind.Object ? Inline(Children(summary)) : "Details";
        return $"**▸ {title}**\n\n{Blocks(content.SelectMany(c => Type(c) == "detailsContent" ? Children(c) : [c]))}";
    }

    // ---------- inline ----------

    private string Inline(IEnumerable<JsonElement> nodes, bool inTable = false)
    {
        var sb = new StringBuilder();
        // Merge adjacent text runs with identical (relevant) marks so "**a****b**" doesn't happen.
        var runs = new List<(string Text, string[] Marks, string? Href)>();
        foreach (var n in nodes)
        {
            switch (Type(n))
            {
                case "text":
                    var marks = Marks(n);
                    var href = n.TryGetProperty("marks", out var ms)
                        ? ms.EnumerateArray().Where(m => Type(m) == "link").Select(m => AttrString(m, "href")).FirstOrDefault()
                        : null;
                    var text = n.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (runs.Count > 0 && runs[^1].Marks.SequenceEqual(marks) && runs[^1].Href == href)
                        runs[^1] = (runs[^1].Text + text, marks, href);
                    else runs.Add((text, marks, href));
                    break;
                case "hardBreak":
                    runs.Add((inTable ? " " : "\n", ["break"], null));
                    break;
                case "mention":
                    runs.Add(("@" + (AttrString(n, "label") ?? ""), [], null));
                    break;
                case "mathInline":
                    runs.Add(("$" + PlainText(n) + "$", ["raw"], null));
                    break;
                default:
                    runs.Add((PlainText(n), [], null));
                    break;
            }
        }

        foreach (var (text, marks, href) in runs)
        {
            if (marks.Contains("break")) { sb.Append(inTable ? " " : "\\\n"); continue; }
            if (marks.Contains("raw")) { sb.Append(text); continue; }
            if (marks.Contains("code")) { sb.Append('`').Append(text.Replace("`", "'")).Append('`'); continue; }

            // Keep surrounding spaces outside the markers: "** bold**" isn't valid markdown.
            var core = text.Trim();
            if (core.Length == 0) { sb.Append(text); continue; }
            var lead = text[..text.IndexOf(core[0])];
            var trail = text[(lead.Length + core.Length)..];
            var s = Escape(core);
            if (marks.Contains("strike")) s = $"~~{s}~~";
            if (marks.Contains("italic")) s = $"*{s}*";
            if (marks.Contains("bold")) s = $"**{s}**";
            if (href is not null) s = $"[{s}]({mapLink(href).Replace(" ", "%20").Replace(")", "%29")})";
            sb.Append(lead).Append(s).Append(trail);
        }
        return sb.ToString();
    }

    /// <summary>Marks that affect markdown (colour, underline and highlight have no markdown form and are dropped).</summary>
    private static string[] Marks(JsonElement text) =>
        !text.TryGetProperty("marks", out var marks) ? [] :
        marks.EnumerateArray().Select(Type).Select(t => t switch
        {
            "bold" or "strong" => "bold",
            "italic" or "em" => "italic",
            "strike" => "strike",
            "code" => "code",
            _ => null,
        }).OfType<string>().Distinct().Order().ToArray();

    private static string Escape(string s) => MarkdownSpecial().Replace(s, @"\$0");

    /// <summary>A paragraph starting with "# ", "- ", "1. " or "&gt;" would turn into something else.</summary>
    private static string EscapeLineStart(string s) => BlockStart().IsMatch(s) ? "\\" + s : s;

    private static string PlainText(JsonElement node)
    {
        if (node.TryGetProperty("text", out var t)) return t.GetString() ?? "";
        return string.Concat(Children(node).Select(PlainText));
    }

    // ---------- JSON helpers ----------

    private static string Type(JsonElement n) => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    private static IEnumerable<JsonElement> Children(JsonElement n) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray() : [];

    private static bool IsBlock(JsonElement n) => Type(n) is not ("text" or "hardBreak" or "mention" or "mathInline");

    private static JsonElement? Attr(JsonElement n, string name) =>
        n.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? AttrString(JsonElement n, string name) => Attr(n, name) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    [GeneratedRegex(@"[\\*_`\[\]<]")] private static partial Regex MarkdownSpecial();
    [GeneratedRegex(@"^(#{1,6}\s|[-+]\s|\d+[.)]\s|>)")] private static partial Regex BlockStart();
}
