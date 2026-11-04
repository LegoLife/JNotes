using JNotes.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Data;

public static class DataServiceExtensions
{
    public static IServiceCollection AddJNotesData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<JNotesDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INoteStore, NoteStore>();
        services.AddSingleton<ITemplateStore, TemplateStore>();
        services.AddSingleton<INoteSearch, NoteSearch>();
        services.AddSingleton<NoteFactory>();
        services.AddSingleton<ICommandStore, CommandStore>();
        services.AddSingleton<NoteEvents>();
        services.AddSingleton<INoteHistory, NoteHistory>();
        services.AddSingleton<ITrash, TrashStore>();
        services.AddSingleton<NoteOperations>();
        services.AddSingleton<AiRequestProcessor>(); // needs an IAiGenerator registered by the host
        return services;
    }

    public static async Task MigrateJNotesDatabaseAsync(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<JNotesDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        // WAL: better concurrency between UI saves and (later) MCP writes / backups.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}
