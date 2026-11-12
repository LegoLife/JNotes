using System.Security.Claims;
using JNotes.Core;
using Microsoft.AspNetCore.Components.Authorization;

namespace JNotes.Demo;

/// <summary>Everyone using the demo is the signed-in owner.</summary>
public sealed class DemoAuthState : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> State = Task.FromResult(new AuthenticationState(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Demo")], "demo"))));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => State;
}

/// <summary>
/// Stands in for Claude Code, which the real app runs headless on the server. Shows what the request and its
/// context look like instead of calling a model.
/// </summary>
public sealed class DemoAiGenerator : IAiGenerator
{
    public async Task<string> GenerateAsync(GenerationContext context, CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.2), ct); // a little "thinking" so the pending state is visible
        var words = context.NoteMarkdown.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        return $"""
            *This is the demo, so no model was called.* In the real app this request goes to Claude Code, which answers in Markdown and the answer lands right here.

            What it would have been sent:

            - **Request:** {context.Prompt}
            - **Note:** "{context.NoteTitle}" in *{(string.IsNullOrEmpty(context.FolderPath) ? "Top level" : context.FolderPath)}*, about {words:N0} words
            - **Attachments:** {(context.Attachments.Count == 0 ? "none" : string.Join(", ", context.Attachments.Select(a => a.Name)))}

            Keep the answer with **Accept**, or throw it away with **Discard**.
            """;
    }
}
