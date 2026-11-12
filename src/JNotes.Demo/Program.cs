using JNotes.Core;
using JNotes.Data;
using JNotes.Demo;
using JNotes.Web.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

// The real data layer. SQLite runs inside the page; its files live in the browser's in-memory file system.
builder.Services.AddJNotesData("Data Source=/data/jnotes.db");
builder.Services.AddSingleton(new AttachmentStore("/data/attachments"));
builder.Services.AddSingleton<AttachmentReader>();
builder.Services.AddSingleton<Exporter>();
builder.Services.AddSingleton(sp => new BackupService(
    sp.GetRequiredService<IDbContextFactory<JNotesDbContext>>(), sp.GetRequiredService<TimeProvider>(), "/data/backups"));
builder.Services.AddSingleton<IAiGenerator, DemoAiGenerator>();

// Every visitor is signed in.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, DemoAuthState>();

var host = builder.Build();
Directory.CreateDirectory("/data");
// Migrations (not EnsureCreated) so the FTS5 search table and its triggers exist. No WAL: there's no shared memory here.
await using (var db = await host.Services.GetRequiredService<IDbContextFactory<JNotesDbContext>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();
await DemoNotes.SeedAsync(host.Services.GetRequiredService<INoteStore>());
await host.RunAsync();
