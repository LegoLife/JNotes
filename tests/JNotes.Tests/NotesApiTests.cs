using System.Net;
using System.Net.Http.Json;
using JNotes.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace JNotes.Tests;

/// <summary>The REST notes API other apps (ReadAlong) use: X-Api-Key, create / read / append.</summary>
public sealed class NotesApiTests : IAsyncLifetime
{
    private const string Key = "api-key-456";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"jnotes-api-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("JNotes:DataDirectory", _dataDir)
            .UseSetting("JNotes:PasswordHash", PasswordVerifier.Hash("x"))
            .UseSetting("JNotes:ApiKey", Key));
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private HttpClient Client(string? key = Key)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (key != null) c.DefaultRequestHeaders.Add("X-Api-Key", key);
        return c;
    }

    private sealed record Dto(Guid Id, string Title, string Folder, DateTimeOffset Updated, string Markdown);

    [Fact]
    public async Task Replaces_whole_note_and_detects_conflicts()
    {
        var http = Client();
        var n = await (await http.PostAsJsonAsync("/api/notes", new { title = "Edited", markdown = "one" })).Content.ReadFromJsonAsync<Dto>();
        var saved = await http.PutAsJsonAsync($"/api/notes/{n!.Id}", new { markdown = "two", baseUpdated = n.Updated, source = "ReadAlong" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = await saved.Content.ReadFromJsonAsync<Dto>();
        Assert.Equal("two", after!.Markdown);

        // Saving again from the stale version is refused and returns the current text.
        var stale = await http.PutAsJsonAsync($"/api/notes/{n.Id}", new { markdown = "three", baseUpdated = n.Updated });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("two", (await stale.Content.ReadFromJsonAsync<Dto>())!.Markdown);

        // From the fresh version it saves.
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync($"/api/notes/{n.Id}", new { markdown = "three", baseUpdated = after.Updated })).StatusCode);
    }

    [Fact]
    public async Task Rejects_missing_or_wrong_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync($"/api/notes/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("nope").PostAsJsonAsync("/api/notes", new { title = "x" })).StatusCode);
    }

    [Fact]
    public async Task Creates_reads_and_appends()
    {
        var http = Client();
        var created = await (await http.PostAsJsonAsync("/api/notes", new { title = "Dune – Frank Herbert", folder = "Reading", markdown = "**Frank Herbert**" }))
            .Content.ReadFromJsonAsync<Dto>();
        Assert.NotNull(created);
        Assert.Equal("Reading", created!.Folder);

        var app = await http.PostAsJsonAsync($"/api/notes/{created.Id}/append", new { markdown = "> Fear is the mind-killer." });
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);

        var read = await http.GetFromJsonAsync<Dto>($"/api/notes/{created.Id}");
        Assert.Contains("Frank Herbert", read!.Markdown);
        Assert.Contains("Fear is the mind-killer.", read.Markdown);
    }

    [Fact]
    public async Task Unknown_note_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/notes/{Guid.NewGuid()}")).StatusCode);
    }
}
