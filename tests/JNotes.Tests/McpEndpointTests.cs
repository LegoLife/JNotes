using System.Net;
using JNotes.Core;
using JNotes.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace JNotes.Tests;

/// <summary>Runs the real app in-memory and talks to /mcp with the official MCP client.</summary>
public sealed class McpEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-123";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"jnotes-mcp-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("JNotes:DataDirectory", _dataDir)
            .UseSetting("JNotes:PasswordHash", PasswordVerifier.Hash("x"))
            .UseSetting("JNotes:McpToken", Token));
        _ = _factory.Server; // start
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private async Task<McpClient> ConnectAsync(string token = Token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_factory.Server.BaseAddress, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, _factory.CreateClient(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Rejects_missing_or_wrong_token()
    {
        using var http = _factory.CreateClient();
        var res = await http.PostAsync("/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("wrong"));
    }

    [Fact]
    public async Task Exposes_the_agreed_tools_and_no_destructive_ones()
    {
        await using var client = await ConnectAsync();
        var names = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToList();

        Assert.Equal(["append_to_note", "create_note", "fulfill_request", "list_attachments", "list_folders", "list_notes", "list_pending_requests",
            "list_templates", "move_note", "read_attachment", "read_note", "replace_section", "search_notes"], names);
    }

    [Fact]
    public async Task Claude_can_find_and_fulfil_an_ai_request()
    {
        await using var client = await ConnectAsync();

        var created = await client.CallToolAsync("create_note", new Dictionary<string, object?>
        {
            ["title"] = "Chapter 1", ["folder"] = "Books/Dune", ["template"] = "Book chapter",
        });
        Assert.True(created.IsError != true, Text(created));

        var pending = Text(await client.CallToolAsync("list_pending_requests", new Dictionary<string, object?>()));
        Assert.Contains("\"requestId\":\"summary\"", pending);

        var fulfilled = await client.CallToolAsync("fulfill_request", new Dictionary<string, object?>
        {
            ["note"] = "Books/Dune/Chapter 1", ["request_id"] = "summary", ["markdown"] = "Paul arrives on **Arrakis**.",
        });
        Assert.True(fulfilled.IsError != true, Text(fulfilled));

        var read = Text(await client.CallToolAsync("read_note", new Dictionary<string, object?> { ["note"] = "Chapter 1" }));
        Assert.Contains(":::ai-result{id=\\u0022summary\\u0022", read);
        Assert.Contains("Paul arrives on **Arrakis**.", read);

        var search = Text(await client.CallToolAsync("search_notes", new Dictionary<string, object?> { ["query"] = "arrakis" }));
        Assert.Contains("Chapter 1", search);
    }

    [Fact]
    public async Task Attachments_can_be_listed_and_read()
    {
        var store = _factory.Services.GetRequiredService<AttachmentStore>();
        var image = await store.SaveAsync(new MemoryStream(AttachmentStoreTests.Png));
        var file = await store.SaveFileAsync(new MemoryStream("Packing list: tent, stove"u8.ToArray()), "Trip list.txt");
        var fileUrl = AttachmentStore.UrlPrefix + file[..33] + Uri.EscapeDataString(file[33..]);
        await using var client = await ConnectAsync();
        await client.CallToolAsync("create_note", new Dictionary<string, object?>
        {
            ["title"] = "Camping", ["markdown"] = $"![map](/attachments/{image})\n\n[📎 Trip list.txt]({fileUrl})",
        });

        var list = Text(await client.CallToolAsync("list_attachments", new Dictionary<string, object?> { ["note"] = "Camping" }));
        Assert.Contains(fileUrl, list);
        Assert.Contains("map", list);

        var text = await client.CallToolAsync("read_attachment", new Dictionary<string, object?> { ["url"] = fileUrl });
        Assert.Contains("Packing list: tent, stove", Text(text));
        var picture = await client.CallToolAsync("read_attachment", new Dictionary<string, object?> { ["url"] = $"/attachments/{image}" });
        Assert.Equal("image/png", picture.Content.OfType<ImageContentBlock>().Single().MimeType);
    }

    [Fact]
    public async Task Tool_errors_are_reported_to_the_model_not_thrown()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync("append_to_note", new Dictionary<string, object?> { ["note"] = "Nope", ["markdown"] = "x" });

        Assert.True(result.IsError == true);
        Assert.Contains("No note titled", Text(result));
    }

    [Fact]
    public async Task Commands_are_served_as_prompts_with_arguments()
    {
        await using var client = await ConnectAsync();

        var prompts = await client.ListPromptsAsync();
        var summary = prompts.Single(p => p.Name == "chapter-summary");
        Assert.Equal(["chapter", "book"], summary.ProtocolPrompt.Arguments!.Select(a => a.Name));

        var result = await client.GetPromptAsync("chapter-summary", new Dictionary<string, object?> { ["book"] = "Dune", ["chapter"] = "3" });
        var text = ((TextContentBlock)result.Messages.Single().Content).Text;
        Assert.StartsWith("Write a summary of chapter 3 of Dune.", text);
        Assert.Contains("Ground rules", text);
    }

    [Fact]
    public async Task Edits_are_broadcast_to_open_editors()
    {
        var events = _factory.Services.GetRequiredService<NoteEvents>();
        var edits = new List<NoteEdit>();
        events.ExternalEdit += edits.Add;
        await using var client = await ConnectAsync();
        await client.CallToolAsync("create_note", new Dictionary<string, object?> { ["title"] = "Log" });

        await client.CallToolAsync("append_to_note", new Dictionary<string, object?> { ["note"] = "Log", ["markdown"] = "hello" });

        var edit = Assert.IsType<AppendEdit>(Assert.Single(edits));
        Assert.Equal("hello", edit.Markdown);
    }
}
