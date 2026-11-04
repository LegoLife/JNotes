using JNotes.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace JNotes.Web.Mcp;

public static class McpSetup
{
    public const string Instructions = """
        JNotes is the user's personal notes app (folders of markdown notes). Use these tools to find, read and write notes.
        - The user leaves ✨ AI requests in notes by typing /ai. Find them with list_pending_requests and answer each with fulfill_request; they see your answer in place and can accept or discard it.
        - Prefer fulfill_request / replace_section / append_to_note over creating new notes, and never try to delete or overwrite whole notes (there are no tools for that).
        - Ground what you write in the user's own notes first; flag anything you're unsure about.
        - Write clean markdown. Don't repeat the note title as a heading and don't write :::ai blocks yourself.
        """;

    public static IServiceCollection AddJNotesMcp(this IServiceCollection services, string? token)
    {
        services.AddSingleton<IMcpAuthenticator>(new StaticTokenAuthenticator(token));
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "JNotes", Version = "1.0" };
                o.ServerInstructions = Instructions;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<NoteTools>()
            // Commands are stored in the database and editable in the app, so prompts are served dynamically.
            .WithListPromptsHandler(async (request, ct) =>
            {
                var commands = await request.Services!.GetRequiredService<ICommandStore>().ListAsync(ct);
                return new ListPromptsResult
                {
                    Prompts = commands.Select(c => new Prompt
                    {
                        Name = c.Name,
                        Title = c.Title,
                        Description = c.Description,
                        Arguments = CommandPrompts.Arguments(c.Body)
                            .Select(a => new PromptArgument { Name = a, Description = a, Required = true }).ToList(),
                    }).ToList(),
                };
            })
            .WithGetPromptHandler(async (request, ct) =>
            {
                var name = request.Params?.Name ?? "";
                var command = await request.Services!.GetRequiredService<ICommandStore>().GetByNameAsync(name, ct)
                              ?? throw new McpException($"Unknown command “{name}”.");
                var args = request.Params?.Arguments?.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.ToString());
                return new GetPromptResult
                {
                    Description = command.Description,
                    Messages =
                    [
                        new PromptMessage
                        {
                            Role = Role.User,
                            Content = new TextContentBlock { Text = CommandPrompts.Render(command.Body, args) },
                        },
                    ],
                };
            });
        return services;
    }
}
