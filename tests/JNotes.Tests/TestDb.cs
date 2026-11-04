using JNotes.Core;
using JNotes.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests;

/// <summary>Stores over a real, migrated SQLite file (plus an attachments dir) that are deleted on dispose.</summary>
public sealed class TestDb : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"jnotes-test-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    private TestDb()
    {
        _services = new ServiceCollection().AddJNotesData($"Data Source={_path}").BuildServiceProvider();
        AttachmentsDir = _path + "-attachments";
        Attachments = new AttachmentStore(AttachmentsDir);
    }

    public string AttachmentsDir { get; }
    public AttachmentStore Attachments { get; }
    public INoteStore Store => _services.GetRequiredService<INoteStore>();
    public ITemplateStore Templates => _services.GetRequiredService<ITemplateStore>();
    public INoteSearch Search => _services.GetRequiredService<INoteSearch>();
    public NoteFactory Factory => _services.GetRequiredService<NoteFactory>();

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
        if (Directory.Exists(AttachmentsDir)) Directory.Delete(AttachmentsDir, recursive: true);
    }
}
