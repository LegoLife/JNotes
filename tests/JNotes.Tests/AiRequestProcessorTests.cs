using JNotes.Core;

namespace JNotes.Tests;

public class AiRequestProcessorTests
{
    private sealed class FakeGenerator(Func<GenerationContext, string> answer) : IAiGenerator
    {
        public GenerationContext? Last { get; private set; }

        public Task<string> GenerateAsync(GenerationContext context, CancellationToken ct = default)
        {
            Last = context;
            return Task.FromResult(answer(context));
        }
    }

    private const string Note = "## Highlights\n\n- spice\n\n## Summary\n\n:::ai-request{id=\"r1\"}\nSummarise it\n:::\n";

    [Fact]
    public async Task Generates_and_fills_the_request_with_a_live_edit()
    {
        await using var db = await TestDb.CreateAsync();
        var folder = await db.Store.CreateFolderAsync("Books", null);
        var note = await db.Store.CreateNoteAsync("Dune", folder.Id, Note);
        var generator = new FakeGenerator(_ => "```markdown\nThe spice must flow.\n```");
        NoteEdit? edit = null;
        db.Events.ExternalEdit += e => edit = e;

        await new AiRequestProcessor(db.Store, db.Ops, generator, db.Events).RunAsync(note.Id, "r1");

        // The model saw the whole note, with a marker where the request was.
        Assert.Equal("Summarise it", generator.Last!.Prompt);
        Assert.Equal("Books", generator.Last.FolderPath);
        Assert.Contains("- spice", generator.Last.NoteMarkdown);
        Assert.Contains(AiPrompt.RequestMarker, generator.Last.NoteMarkdown);
        Assert.DoesNotContain(":::ai-request", generator.Last.NoteMarkdown);

        var content = (await db.Store.GetNoteAsync(note.Id))!.Content;
        Assert.Contains(":::ai-result{id=\"r1\" prompt=\"Summarise it\"}\nThe spice must flow.\n:::", content); // outer fence stripped
        Assert.IsType<RequestFulfilledEdit>(edit);
    }

    [Fact]
    public async Task Failures_are_reported_and_leave_the_request_pending()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Store.CreateNoteAsync("Dune", null, Note);
        GenerationFailure? failure = null;
        db.Events.GenerationFailed += f => failure = f;
        var generator = new FakeGenerator(_ => throw new GenerationException("You've hit your Claude usage limit; try again later."));

        await new AiRequestProcessor(db.Store, db.Ops, generator, db.Events).RunAsync(note.Id, "r1");

        Assert.Equal(new GenerationFailure(note.Id, "r1", "You've hit your Claude usage limit; try again later."), failure);
        Assert.Single(AiBlocks.FindRequests((await db.Store.GetNoteAsync(note.Id))!.Content));
    }

    [Fact]
    public async Task A_request_removed_before_generation_is_reported()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Store.CreateNoteAsync("Dune", null, "nothing pending");
        GenerationFailure? failure = null;
        db.Events.GenerationFailed += f => failure = f;

        await new AiRequestProcessor(db.Store, db.Ops, new FakeGenerator(_ => "x"), db.Events).RunAsync(note.Id, "r1");

        Assert.Contains("isn't in the note", failure!.Message);
    }

    [Fact]
    public async Task Start_ignores_duplicate_clicks_while_running()
    {
        await using var db = await TestDb.CreateAsync();
        var note = await db.Store.CreateNoteAsync("Dune", null, Note);
        var gate = new TaskCompletionSource();
        var slow = new SlowGenerator(gate.Task);
        var processor = new AiRequestProcessor(db.Store, db.Ops, slow, db.Events);

        Assert.True(processor.Start(note.Id, "r1"));
        Assert.False(processor.Start(note.Id, "r1"));
        Assert.True(processor.IsRunning(note.Id, "r1"));

        gate.SetResult();
        for (var i = 0; i < 100 && processor.IsRunning(note.Id, "r1"); i++) await Task.Delay(20);
        Assert.False(processor.IsRunning(note.Id, "r1"));
        Assert.Empty(AiBlocks.FindRequests((await db.Store.GetNoteAsync(note.Id))!.Content));
    }

    private sealed class SlowGenerator(Task gate) : IAiGenerator
    {
        public async Task<string> GenerateAsync(GenerationContext context, CancellationToken ct = default)
        {
            await gate;
            return "done";
        }
    }

    [Fact]
    public void Clean_answer_only_strips_a_wrapping_fence()
    {
        Assert.Equal("a\nb", AiPrompt.CleanAnswer("```markdown\na\nb\n```"));
        const string codeInside = "Intro\n\n```js\nx()\n```";
        Assert.Equal(codeInside, AiPrompt.CleanAnswer(codeInside));
    }
}
