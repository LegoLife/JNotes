using JNotes.Data;
using JNotes.Web.Auth;
using JNotes.Web.Components;
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

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();
app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

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
