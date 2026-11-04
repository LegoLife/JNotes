using JNotes.Core;
using JNotes.Data;
using JNotes.Web.Ai;
using JNotes.Web.Auth;
using JNotes.Web.Components;
using JNotes.Web.Maintenance;
using JNotes.Web.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;

// `dotnet JNotes.Web.dll hash-password [password]` prints a hash for JNotes__PasswordHash.
if (args is ["hash-password", .. var rest])
{
    var pw = rest.Length > 0 ? rest[0] : ReadPassword();
    if (string.IsNullOrEmpty(pw)) { Console.Error.WriteLine("Password can't be empty."); return 1; }
    Console.WriteLine(PasswordVerifier.Hash(pw));
    return 0;
}
// `dotnet JNotes.Web.dll generate-token` prints a random token for JNotes__McpToken.
if (args is ["generate-token", ..])
{
    Console.WriteLine(StaticTokenAuthenticator.Generate());
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.GetFullPath(builder.Configuration["JNotes:DataDirectory"]
                               ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
Directory.CreateDirectory(dataDir);

var passwordHash = builder.Configuration["JNotes:PasswordHash"];
if (string.IsNullOrWhiteSpace(passwordHash))
    throw new InvalidOperationException(
        "JNotes:PasswordHash is not set. Generate one with `docker run --rm -it jnotes hash-password` " +
        "(or `dotnet run -- hash-password`) and set it as the JNotes__PasswordHash environment variable.");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024); // large notes over SignalR
builder.Services.AddMudServices();

builder.Services.AddJNotesData($"Data Source={Path.Combine(dataDir, "jnotes.db")}");
builder.Services.AddSingleton(new AttachmentStore(Path.Combine(dataDir, "attachments")));
builder.Services.AddSingleton<DocmostImporter>();
builder.Services.AddSingleton<Exporter>();
builder.Services.AddSingleton(sp => new BackupService(
    sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<JNotesDbContext>>(),
    sp.GetRequiredService<TimeProvider>(), Path.Combine(dataDir, "backups")));
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddJNotesMcp(builder.Configuration["JNotes:McpToken"]);

// Inline ✨ generation via Claude Code headless (uses the Claude subscription; no API key).
var claudeOptions = builder.Configuration.GetSection("JNotes:Claude").Get<ClaudeCodeOptions>() ?? new ClaudeCodeOptions();
builder.Services.AddSingleton(claudeOptions);
builder.Services.AddSingleton<IAiGenerator, ClaudeCodeGenerator>();
builder.Services.AddSingleton(new PasswordVerifier(passwordHash));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("JNotes");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.Name = "jnotes.auth";
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // plain HTTP on the LAN
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Proxy-agnostic: honour X-Forwarded-* from whatever tunnel/proxy sits in front.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await app.Services.MigrateJNotesDatabaseAsync();

// One-off migration from Docmost's database (see DocmostDbImportCommand); runs without starting the web server.
if (args is ["import-docmost-db", var importDir, .. var importOptions])
    return await JNotes.Web.Cli.DocmostDbImportCommand.RunAsync(app.Services, importDir, importOptions.Contains("--dry-run"));

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseMcpAuth(); // bearer token for /mcp; the cookie login below is for the web UI
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();
app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// Images: uploaded as a raw request body (type sniffed server-side), served only to the signed-in user.
var attachmentsApi = app.MapGroup("/attachments").RequireAuthorization();
attachmentsApi.MapPost("", async (HttpRequest request, AttachmentStore store, CancellationToken ct) =>
{
    try
    {
        var name = await store.SaveAsync(request.Body, ct);
        return Results.Ok(new { url = AttachmentStore.UrlPrefix + name });
    }
    catch (DomainException ex)
    {
        return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
});
attachmentsApi.MapGet("{name}", (string name, AttachmentStore store, HttpResponse response) =>
{
    if (store.GetPath(name) is not { } path) return Results.NotFound();
    response.Headers.CacheControl = "private, max-age=31536000, immutable"; // names are unique per upload
    response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(path, AttachmentStore.ContentTypeFor(name), enableRangeProcessing: true);
});

// Export: whole vault as a zip, or one note as .md (.zip when it has images).
var exportApi = app.MapGroup("/export").RequireAuthorization();
exportApi.MapGet("vault", async (Exporter exporter, CancellationToken ct) =>
{
    // ZipArchive writes synchronously when it finishes, so build it in a temp file and stream that.
    var temp = Path.Combine(Path.GetTempPath(), $"jnotes-export-{Guid.NewGuid():N}.zip");
    await using (var fs = File.Create(temp))
        await exporter.WriteVaultAsync(fs, ct);
    var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    return Results.File(stream, "application/zip", $"JNotes-export-{DateTime.Now:yyyy-MM-dd}.zip");
});
exportApi.MapGet("notes/{id:guid}", async (Guid id, Exporter exporter, CancellationToken ct) =>
{
    try
    {
        var file = await exporter.ExportNoteAsync(id, ct);
        return Results.File(file.Data, file.ContentType, file.FileName);
    }
    catch (DomainException) { return Results.NotFound(); }
});

app.MapMcp("/mcp");
if (!app.Services.GetRequiredService<IMcpAuthenticator>().IsConfigured)
    app.Logger.LogWarning("JNotes__McpToken is not set; the Claude (MCP) endpoint is disabled.");

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
return 0;

static string? ReadPassword()
{
    Console.Error.Write("Password: ");
    if (Console.IsInputRedirected) return Console.ReadLine();
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
        chars.Add(key.KeyChar);
    }
    Console.Error.WriteLine();
    return new string(chars.ToArray());
}

public partial class Program;
