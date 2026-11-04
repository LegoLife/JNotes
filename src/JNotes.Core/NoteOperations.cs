namespace JNotes.Core;

/// <summary>An edit made by Claude (via MCP) that an open editor should apply live.</summary>
public abstract record NoteEdit(Guid NoteId, string NewContent);
public sealed record AppendEdit(Guid NoteId, string NewContent, string Markdown) : NoteEdit(NoteId, NewContent);
public sealed record SectionEdit(Guid NoteId, string NewContent, string Heading, string Markdown) : NoteEdit(NoteId, NewContent);
public sealed record RequestFulfilledEdit(Guid NoteId, string NewContent, string RequestId, string ResultBlock) : NoteEdit(NoteId, NewContent);

/// <summary>In-process bus so open editors hear about Claude's edits.</summary>
public sealed class NoteEvents
{
    public event Action<NoteEdit>? ExternalEdit;
    public void Raise(NoteEdit edit) => ExternalEdit?.Invoke(edit);

    /// <summary>An inline ✨ request couldn't be generated (shown to the user on the open note).</summary>
    public event Action<GenerationFailure>? GenerationFailed;
    public void RaiseGenerationFailed(GenerationFailure failure) => GenerationFailed?.Invoke(failure);
}

public sealed record PendingRequest(Guid NoteId, string NoteTitle, string NotePath, string RequestId, string Prompt, string? Section);

/// <summary>
/// The operations Claude can perform. Notes and folders are referenced by id or by name/path so the
/// model can be forgiving; ambiguity is reported back instead of guessed.
/// </summary>
public sealed class NoteOperations(INoteStore notes, ITemplateStore templates, NoteFactory factory, NoteEvents events, INoteHistory history)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<NoteTreeIndex> TreeAsync(CancellationToken ct = default) => new(await notes.GetTreeAsync(ct));

    public static string PathOf(NoteTreeIndex index, Guid? folderId) =>
        folderId is { } f && index.Find(f) is not null ? index.PathOf(f) : "";

    /// <summary>Resolves a note by id, or by exact (case-insensitive) title, optionally "Folder/Sub/Title".</summary>
    public async Task<Note> ResolveNoteAsync(string reference, CancellationToken ct = default)
    {
        var r = (reference ?? "").Trim();
        if (Guid.TryParse(r, out var id))
            return await notes.GetNoteAsync(id, ct) ?? throw new DomainException($"No note with id {id}.");

        var index = await TreeAsync(ct);
        var (folderPath, title) = SplitPath(r);
        var candidates = (await notes.GetTreeAsync(ct))
            .Where(e => !e.IsFolder && string.Equals(e.Name, title, StringComparison.CurrentCultureIgnoreCase))
            .Where(e => folderPath is null || string.Equals(PathOf(index, e.ParentId).Replace(" / ", "/"), folderPath, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        return candidates.Count switch
        {
            0 => throw new DomainException($"No note titled “{r}”. Use search_notes to find it."),
            1 => (await notes.GetNoteAsync(candidates[0].Id, ct))!,
            _ => throw new DomainException($"Several notes are titled “{title}”: " +
                 string.Join("; ", candidates.Select(c => $"{c.Id} in “{PathOf(index, c.ParentId)}”")) + ". Use the id."),
        };
    }

    /// <summary>Resolves "Books/Dune" or a folder id. Empty/null = top level. Missing folders are created when allowed.</summary>
    public async Task<Guid?> ResolveFolderAsync(string? reference, bool createMissing, CancellationToken ct = default)
    {
        var r = (reference ?? "").Trim().Trim('/');
        if (r.Length == 0) return null;
        if (Guid.TryParse(r, out var id))
            return await notes.GetFolderAsync(id, ct) is not null ? id : throw new DomainException($"No folder with id {id}.");

        var index = await TreeAsync(ct);
        Guid? current = null;
        foreach (var segment in r.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = index.ChildrenOf(current).FirstOrDefault(e => e.IsFolder && string.Equals(e.Name, segment, StringComparison.CurrentCultureIgnoreCase));
            if (match is not null) { current = match.Id; continue; }
            if (!createMissing) throw new DomainException($"Folder “{r}” not found. Use list_folders to see folders.");
            current = (await notes.CreateFolderAsync(segment, current, ct)).Id;
            index = await TreeAsync(ct);
        }
        return current;
    }

    public async Task<IReadOnlyList<PendingRequest>> ListPendingRequestsAsync(string? noteReference, CancellationToken ct = default)
    {
        var index = await TreeAsync(ct);
        var source = noteReference is { Length: > 0 }
            ? [await ResolveNoteAsync(noteReference, ct)]
            : await notes.FindNotesContainingAsync(":::ai-request", ct);
        return source
            .SelectMany(n => AiBlocks.FindRequests(n.Content)
                .Select(r => new PendingRequest(n.Id, n.Title, PathOf(index, n.FolderId), r.Id, r.Prompt, r.Section)))
            .ToList();
    }

    public async Task<Note> CreateNoteAsync(string title, string? folder, string? template, string? content, CancellationToken ct = default)
    {
        var folderId = await ResolveFolderAsync(folder, createMissing: true, ct);
        Guid? templateId = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            var all = await templates.ListAsync(ct);
            templateId = (Guid.TryParse(template, out var tid) ? all.FirstOrDefault(t => t.Id == tid) : null)?.Id
                         ?? all.FirstOrDefault(t => string.Equals(t.Name, template.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id
                         ?? throw new DomainException($"No template named “{template}”. Available: {string.Join(", ", all.Select(t => $"“{t.Name}”"))}.");
        }
        var note = await factory.CreateAsync(title, folderId, templateId, ct);
        if (!string.IsNullOrWhiteSpace(content))
        {
            note.Content = AiBlocks.Append(note.Content, content);
            await notes.SaveContentAsync(note.Id, note.Content, ct);
        }
        return note;
    }

    public Task<Note> AppendAsync(string noteReference, string markdown, CancellationToken ct = default) =>
        EditAsync(noteReference, "Before Claude: added to the end", n =>
        {
            var updated = AiBlocks.Append(n.Content, markdown);
            return (updated, new AppendEdit(n.Id, updated, AiBlocks.SanitizeBody(markdown)));
        }, ct);

    public Task<Note> ReplaceSectionAsync(string noteReference, string heading, string markdown, CancellationToken ct = default) =>
        EditAsync(noteReference, $"Before Claude: rewrote “{heading}”", n =>
        {
            var updated = AiBlocks.ReplaceSection(n.Content, heading, markdown);
            return (updated, new SectionEdit(n.Id, updated, heading, AiBlocks.SanitizeBody(markdown)));
        }, ct);

    public Task<Note> FulfillRequestAsync(string noteReference, string requestId, string markdown, CancellationToken ct = default) =>
        EditAsync(noteReference, "Before Claude: answered a ✨ request", n =>
        {
            var (updated, block) = AiBlocks.Fulfill(n.Content, requestId.Trim(), markdown);
            return (updated, new RequestFulfilledEdit(n.Id, updated, requestId.Trim(), block));
        }, ct);

    public async Task<(Note Note, string Path)> MoveNoteAsync(string noteReference, string? folder, CancellationToken ct = default)
    {
        var note = await ResolveNoteAsync(noteReference, ct);
        var folderId = await ResolveFolderAsync(folder, createMissing: true, ct);
        await notes.MoveNoteAsync(note.Id, folderId, ct);
        note.FolderId = folderId;
        return (note, PathOf(await TreeAsync(ct), folderId));
    }

    private async Task<Note> EditAsync(string noteReference, string reason, Func<Note, (string Updated, NoteEdit Edit)> change, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var note = await ResolveNoteAsync(noteReference, ct);
            var (updated, edit) = change(note);
            await history.SnapshotAsync(note.Id, reason, ct); // so any Claude edit can be undone from History
            await notes.SaveContentAsync(note.Id, updated, ct);
            note.Content = updated;
            events.Raise(edit);
            return note;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static (string? Folder, string Title) SplitPath(string r)
    {
        var slash = r.LastIndexOf('/');
        return slash < 0 ? (null, r) : (r[..slash].Trim().Trim('/').Replace(" / ", "/"), r[(slash + 1)..].Trim());
    }
}
