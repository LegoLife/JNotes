using System.ComponentModel;
using JNotes.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JNotes.Web.Mcp;

/// <summary>
/// MCP tools Claude uses to work with JNotes. Deliberately no delete and no whole-note overwrite:
/// Claude can read, create, append, fill AI requests, replace a section and move notes.
/// </summary>
[McpServerToolType]
public sealed class NoteTools(NoteOperations ops, INoteStore store, INoteSearch search, ITemplateStore templates, AttachmentReader attachments)
{
    private const string NoteParam = "The note's id (preferred, from search/list results) or its exact title, optionally with folder path like \"Books/Dune/Chapter 3\".";

    public sealed record FolderInfo(Guid Id, string Path, int Notes);
    public sealed record NoteInfo(Guid Id, string Title, string Folder, DateTimeOffset Updated);
    public sealed record SearchResult(Guid Id, string Title, string Folder, string Snippet);
    public sealed record NoteContent(Guid Id, string Title, string Folder, DateTimeOffset Updated, IReadOnlyList<string> Headings, string Markdown);
    public sealed record TemplateInfo(Guid Id, string Name, string Markdown);
    public sealed record Pending(Guid NoteId, string NoteTitle, string Folder, string RequestId, string Prompt, string? Section);
    public sealed record WriteResult(Guid Id, string Title, string Folder, string Message);

    [McpServerTool(Name = "list_folders", ReadOnly = true, Destructive = false)]
    [Description("Lists every folder in JNotes with its full path and how many notes it directly contains.")]
    public async Task<IReadOnlyList<FolderInfo>> ListFolders(CancellationToken ct = default)
    {
        var tree = await store.GetTreeAsync(ct);
        var index = new NoteTreeIndex(tree);
        return tree.Where(e => e.IsFolder)
            .Select(f => new FolderInfo(f.Id, index.PathOf(f.Id), index.ChildrenOf(f.Id).Count(c => !c.IsFolder)))
            .OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [McpServerTool(Name = "list_notes", ReadOnly = true, Destructive = false)]
    [Description("Lists the notes directly inside a folder (not subfolders). Omit folder for top-level notes.")]
    public Task<IReadOnlyList<NoteInfo>> ListNotes(
        [Description("Folder path like \"Books/Dune\" or a folder id. Omit for the top level.")] string? folder = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var folderId = await ops.ResolveFolderAsync(folder, createMissing: false, ct);
        var index = await ops.TreeAsync(ct);
        var path = NoteOperations.PathOf(index, folderId);
        var result = new List<NoteInfo>();
        foreach (var e in index.ChildrenOf(folderId).Where(e => !e.IsFolder))
            if (await store.GetNoteAsync(e.Id, ct) is { } n) result.Add(new NoteInfo(n.Id, n.Title, path, n.UpdatedAt));
        return (IReadOnlyList<NoteInfo>)result;
    });

    [McpServerTool(Name = "search_notes", ReadOnly = true, Destructive = false)]
    [Description("Full-text search across all notes (titles and content). Every word must match; word prefixes match. Use \"quotes\" for exact phrases. Matches in snippets are wrapped in **bold**.")]
    public Task<IReadOnlyList<SearchResult>> SearchNotes(
        [Description("What to search for.")] string query,
        [Description("Optional folder path or id to limit the search to (includes subfolders).")] string? folder = null,
        [Description("Maximum results (default 10, max 50).")] int? limit = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var folderId = await ops.ResolveFolderAsync(folder, createMissing: false, ct);
        var index = await ops.TreeAsync(ct);
        var hits = await search.SearchAsync(query, folderId, Math.Clamp(limit ?? 10, 1, 50), ct);
        return (IReadOnlyList<SearchResult>)hits.Select(h => new SearchResult(h.NoteId, h.Title, NoteOperations.PathOf(index, h.FolderId),
            Search.CleanSnippet(h.Snippet).Replace(Search.HighlightStart.ToString(), "**").Replace(Search.HighlightEnd.ToString(), "**"))).ToList();
    });

    [McpServerTool(Name = "read_note", ReadOnly = true, Destructive = false)]
    [Description("Reads a note's full markdown, plus its folder and headings. ✨ AI request blocks appear as :::ai-request{id=\"…\"} … ::: and earlier AI output as :::ai-result … :::.")]
    public Task<NoteContent> ReadNote([Description(NoteParam)] string note, CancellationToken ct = default) => Run(async () =>
    {
        var n = await ops.ResolveNoteAsync(note, ct);
        var index = await ops.TreeAsync(ct);
        return new NoteContent(n.Id, n.Title, NoteOperations.PathOf(index, n.FolderId), n.UpdatedAt,
            AiBlocks.Headings(n.Content).Select(h => $"{new string('#', h.Level)} {h.Text}").ToList(), n.Content);
    });

    [McpServerTool(Name = "list_attachments", ReadOnly = true, Destructive = false)]
    [Description("Lists the files and images attached to a note (name and url). Pass a url to read_attachment to see its contents.")]
    public Task<IReadOnlyList<AttachmentReader.Link>> ListAttachments([Description(NoteParam)] string note, CancellationToken ct = default) => Run(async () =>
        AttachmentReader.FindLinks((await ops.ResolveNoteAsync(note, ct)).Content));

    [McpServerTool(Name = "read_attachment", ReadOnly = true, Destructive = false)]
    [Description("Reads a file or image attached to a note, by the /attachments/… url from the note's markdown or list_attachments. "
        + "Images are returned as images. PDFs, Word (.docx) and text-like files (txt, md, csv, json, code…) are returned as text, "
        + "up to 50,000 characters per call; if more remains, call again with the offset given. Other types (zip, audio, video…) can't be read.")]
    public Task<CallToolResult> ReadAttachment(
        [Description("The attachment url, e.g. /attachments/0123…/Report.pdf")] string url,
        [Description("Character offset to continue reading long text from (default 0).")] int? offset = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var content = await attachments.ReadAsync(url, offset ?? 0, ct);
        var header = $"{content.Name} ({content.Size:N0} bytes)";
        IList<ContentBlock> blocks = content switch
        {
            AttachmentContent.Image i => [new TextContentBlock { Text = header }, ImageContentBlock.FromBytes(i.Data, i.MimeType)],
            AttachmentContent.Text t => [new TextContentBlock
            {
                Text = $"{header}, characters {t.Offset:N0}–{t.Offset + t.Content.Length:N0} of {t.Total:N0}"
                    + (t.Offset + t.Content.Length < t.Total ? $" — more remains: call again with offset={t.Offset + t.Content.Length}" : "")
                    + "\n\n" + t.Content,
            }],
            AttachmentContent.Unreadable u => [new TextContentBlock { Text = $"{header}: {u.Reason}" }],
            _ => throw new InvalidOperationException(),
        };
        return new CallToolResult { Content = blocks };
    });

    [McpServerTool(Name = "list_templates", ReadOnly = true, Destructive = false)]
    [Description("Lists note templates (name and markdown). Templates can be used with create_note.")]
    public async Task<IReadOnlyList<TemplateInfo>> ListTemplates(CancellationToken ct = default) =>
        (await templates.ListAsync(ct)).Select(t => new TemplateInfo(t.Id, t.Name, t.Content)).ToList();

    [McpServerTool(Name = "list_pending_requests", ReadOnly = true, Destructive = false)]
    [Description("Lists ✨ AI requests the user has left in their notes (typed with /ai) that are waiting to be filled in with fulfill_request. Includes the heading each request sits under.")]
    public Task<IReadOnlyList<Pending>> ListPendingRequests(
        [Description("Optional: only this note (id or title). Omit to search all notes.")] string? note = null,
        CancellationToken ct = default) => Run(async () =>
        (IReadOnlyList<Pending>)(await ops.ListPendingRequestsAsync(note, ct))
            .Select(p => new Pending(p.NoteId, p.NoteTitle, p.NotePath, p.RequestId, p.Prompt, p.Section)).ToList());

    [McpServerTool(Name = "create_note", ReadOnly = false, Destructive = false)]
    [Description("Creates a new note. Folders in the path are created if missing. Optionally start from a template (by name) and/or add markdown content at the end.")]
    public Task<WriteResult> CreateNote(
        [Description("Title of the new note.")] string title,
        [Description("Folder path like \"Books/Dune\" (created if missing). Omit for top level.")] string? folder = null,
        [Description("Optional template name, e.g. \"Book chapter\".")] string? template = null,
        [Description("Optional markdown to add (after the template content). Don't repeat the title as a heading.")] string? markdown = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var n = await ops.CreateNoteAsync(title, folder, template, markdown, ct);
        return new WriteResult(n.Id, n.Title, NoteOperations.PathOf(await ops.TreeAsync(ct), n.FolderId), "Note created.");
    });

    [McpServerTool(Name = "append_to_note", ReadOnly = false, Destructive = false)]
    [Description("Adds markdown to the end of a note. Existing content is untouched.")]
    public Task<WriteResult> AppendToNote(
        [Description(NoteParam)] string note,
        [Description("Markdown to append.")] string markdown,
        CancellationToken ct = default) => Write(() => ops.AppendAsync(note, markdown, ct), "Appended.", ct);

    [McpServerTool(Name = "replace_section", ReadOnly = false, Destructive = true)]
    [Description("Replaces the content under a heading (up to the next heading of the same or higher level). The heading itself is kept. Use this to fill or rewrite a section such as \"Summary\".")]
    public Task<WriteResult> ReplaceSection(
        [Description(NoteParam)] string note,
        [Description("The heading text, e.g. \"Summary\" (case-insensitive, without #).")] string heading,
        [Description("New markdown for the section body (don't repeat the heading).")] string markdown,
        CancellationToken ct = default) => Write(() => ops.ReplaceSectionAsync(note, heading, markdown, ct), $"Section “{heading}” replaced.", ct);

    [McpServerTool(Name = "fulfill_request", ReadOnly = false, Destructive = false)]
    [Description("Fills in a pending ✨ AI request (from list_pending_requests) with generated markdown. The user then sees it in place and can accept or discard it.")]
    public Task<WriteResult> FulfillRequest(
        [Description(NoteParam)] string note,
        [Description("The request id from list_pending_requests.")] string request_id,
        [Description("The generated markdown that answers the request.")] string markdown,
        CancellationToken ct = default) => Write(() => ops.FulfillRequestAsync(note, request_id, markdown, ct), "Request filled in; the user can accept or discard it.", ct);

    [McpServerTool(Name = "move_note", ReadOnly = false, Destructive = false)]
    [Description("Moves a note into a folder (created if missing). Use an empty folder to move it to the top level.")]
    public Task<WriteResult> MoveNote(
        [Description(NoteParam)] string note,
        [Description("Destination folder path like \"Books/Dune\", or empty for the top level.")] string? folder = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var (n, path) = await ops.MoveNoteAsync(note, folder, ct);
        return new WriteResult(n.Id, n.Title, path, path.Length == 0 ? "Moved to the top level." : $"Moved to “{path}”.");
    });

    private Task<WriteResult> Write(Func<Task<Note>> action, string message, CancellationToken ct = default) => Run(async () =>
    {
        var n = await action();
        return new WriteResult(n.Id, n.Title, NoteOperations.PathOf(await ops.TreeAsync(ct), n.FolderId), message);
    });

    /// <summary>Domain errors become tool errors the model can read and recover from.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DomainException ex) { throw new McpException(ex.Message); }
    }
}
