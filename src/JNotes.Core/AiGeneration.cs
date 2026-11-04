using System.Collections.Concurrent;

namespace JNotes.Core;

/// <summary>Everything the model needs to answer one inline request.</summary>
public sealed record GenerationContext(string NoteTitle, string FolderPath, string NoteMarkdown, string Prompt);

/// <summary>Produces markdown for an inline ✨ request. Implemented by Claude Code (headless) in the web app.</summary>
public interface IAiGenerator
{
    Task<string> GenerateAsync(GenerationContext context, CancellationToken ct = default);
}

public static class AiPrompt
{
    public const string RequestMarker = "⟦REQUEST HERE⟧";

    public const string System = """
        You are writing inside the user's personal notes app (JNotes). The user left a request at a specific spot in one of their notes.
        Reply with ONLY the markdown to insert at that spot: no preamble, no explanation, no closing remarks, and don't wrap the whole answer in a code block.
        Don't repeat the note title, the request, or the heading the request sits under.
        Ground your answer in the note's own content first. Where you add your own knowledge, only do so when confident, and briefly flag anything uncertain (e.g. "*Unsure:* …").
        Match the note's language and tone, and keep the answer as long as the request needs and no longer.
        """;

    public static string User(GenerationContext c) => $"""
        Note: "{c.NoteTitle}"{(c.FolderPath.Length > 0 ? $" (folder: {c.FolderPath})" : "")}

        <note>
        {c.NoteMarkdown}
        </note>

        Request (to be answered at {RequestMarker}): {c.Prompt}
        """;

    /// <summary>Strips an outer ```markdown fence if the model wrapped its whole answer in one.</summary>
    public static string CleanAnswer(string answer)
    {
        var text = answer.Trim();
        if (text.StartsWith("```") && text.EndsWith("```") && text.Count(c => c == '\n') >= 2)
        {
            var firstNewline = text.IndexOf('\n');
            var inner = text[(firstNewline + 1)..^3];
            if (!inner.Contains("\n```")) text = inner.Trim();
        }
        return text;
    }
}

/// <summary>
/// Runs inline ✨ requests in the background: generate, then fill the request through the same
/// path Claude Desktop uses (so open editors update live). Failures are reported via <see cref="NoteEvents"/>.
/// </summary>
public sealed class AiRequestProcessor(INoteStore notes, NoteOperations ops, IAiGenerator generator, NoteEvents events)
{
    private readonly ConcurrentDictionary<(Guid, string), byte> _inFlight = new();
    private readonly SemaphoreSlim _slots = new(2, 2); // the Pi shouldn't run many Claude processes at once

    public bool IsRunning(Guid noteId, string requestId) => _inFlight.ContainsKey((noteId, requestId));

    /// <summary>Starts generation in the background. Returns false if this request is already being generated.</summary>
    public bool Start(Guid noteId, string requestId)
    {
        if (!_inFlight.TryAdd((noteId, requestId), 0)) return false;
        _ = Task.Run(() => RunAsync(noteId, requestId));
        return true;
    }

    /// <summary>Generates and fills one request. Exposed for tests; normally use <see cref="Start"/>.</summary>
    public async Task RunAsync(Guid noteId, string requestId, CancellationToken ct = default)
    {
        _inFlight.TryAdd((noteId, requestId), 0);
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                var note = await notes.GetNoteAsync(noteId, ct) ?? throw new DomainException("The note was deleted.");
                var request = AiBlocks.FindRequests(note.Content).FirstOrDefault(r => r.Id == requestId)
                              ?? throw new DomainException("The request isn't in the note any more.");
                var path = NoteOperations.PathOf(await ops.TreeAsync(ct), note.FolderId);
                var markdown = AiBlocks.MarkRequest(note.Content, requestId, AiPrompt.RequestMarker);

                var answer = AiPrompt.CleanAnswer(await generator.GenerateAsync(new GenerationContext(note.Title, path, markdown, request.Prompt), ct));
                if (answer.Length == 0) throw new DomainException("Claude returned an empty answer.");

                await ops.FulfillRequestAsync(noteId.ToString(), requestId, answer, ct);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            events.RaiseGenerationFailed(new GenerationFailure(noteId, requestId, ex is DomainException or GenerationException ? ex.Message : "Something went wrong while generating."));
        }
        finally
        {
            _inFlight.TryRemove((noteId, requestId), out _);
        }
    }
}

/// <summary>A generator failure with a message that's safe and useful to show the user.</summary>
public sealed class GenerationException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record GenerationFailure(Guid NoteId, string RequestId, string Message);
