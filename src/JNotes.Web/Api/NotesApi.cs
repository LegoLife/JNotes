using System.Security.Cryptography;
using System.Text;
using JNotes.Core;
using Microsoft.AspNetCore.Diagnostics;

namespace JNotes.Web.Api;

/// <summary>
/// A small REST API for other apps on the LAN (e.g. ReadAlong adding reading notes). Separate from MCP/Claude:
/// its own key in JNotes:ApiKey, sent as "X-Api-Key". Read, create and append only — no delete or overwrite.
/// </summary>
public static class NotesApi
{
    public sealed record CreateReq(string Title, string? Folder, string? Markdown);
    public sealed record AppendReq(string Markdown);
    public sealed record NoteDto(Guid Id, string Title, string Folder, DateTimeOffset Updated, string Markdown);

    public static void MapNotesApi(this WebApplication app)
    {
        var key = app.Configuration["JNotes:ApiKey"]?.Trim();
        var expected = string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
        if (expected is null) app.Logger.LogInformation("JNotes__ApiKey is not set; the notes API (/api/notes) is disabled.");

        var api = app.MapGroup("/api/notes").AllowAnonymous().AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false; // real status codes, not the HTML pages
            if (expected is null) return Results.Text("The notes API is disabled: set JNotes__ApiKey.", statusCode: 503);
            var presented = Encoding.UTF8.GetBytes(http.Request.Headers["X-Api-Key"].ToString().Trim());
            if (!CryptographicOperations.FixedTimeEquals(presented, expected)) return Results.Unauthorized();
            try { return await next(ctx); }
            catch (DomainException ex) { return Results.Text(ex.Message, statusCode: ex.Message.StartsWith("No note") ? 404 : 400); }
        });

        api.MapGet("{id:guid}", async (Guid id, NoteOperations ops, CancellationToken ct) =>
        {
            var n = await ops.ResolveNoteAsync(id.ToString(), ct);
            return Results.Ok(new NoteDto(n.Id, n.Title, NoteOperations.PathOf(await ops.TreeAsync(ct), n.FolderId), n.UpdatedAt, n.Content));
        });

        api.MapPost("", async (CreateReq req, NoteOperations ops, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.Text("A title is required.", statusCode: 400);
            var n = await ops.CreateNoteAsync(req.Title.Trim(), req.Folder, null, req.Markdown, ct);
            return Results.Ok(new NoteDto(n.Id, n.Title, NoteOperations.PathOf(await ops.TreeAsync(ct), n.FolderId), n.UpdatedAt, n.Content));
        });

        api.MapPost("{id:guid}/append", async (Guid id, AppendReq req, NoteOperations ops, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Markdown)) return Results.Text("Nothing to append.", statusCode: 400);
            var n = await ops.AppendAsync(id.ToString(), req.Markdown, ct);
            return Results.Ok(new { n.Id, n.Title, n.UpdatedAt });
        });
    }
}
