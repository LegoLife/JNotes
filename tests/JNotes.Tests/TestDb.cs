using JNotes.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests;

/// <summary>A NoteStore over a real, migrated SQLite file that is deleted on dispose.</summary>
public sealed class TestDb : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"jnotes-test-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    private TestDb()
    {
        _services = new ServiceCollection().AddJNotesData($"Data Source={_path}").BuildServiceProvider();
    }

    public NoteStore Store => (NoteStore)_services.GetRequiredService<JNotes.Core.INoteStore>();

    public static async Task<TestDb> CreateAsync()
    {
        var db = new TestDb();
        await db._services.MigrateJNotesDatabaseAsync();
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
