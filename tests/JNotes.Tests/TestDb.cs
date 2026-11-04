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

    private TestDb(TimeProvider? clock)
    {
        var services = new ServiceCollection().AddJNotesData($"Data Source={_path}");
        if (clock is not null) services.AddSingleton(clock); // last registration wins
        _services = services.BuildServiceProvider();
        AttachmentsDir = _path + "-attachments";
        Attachments = new AttachmentStore(AttachmentsDir);
    }

    public string AttachmentsDir { get; }
    public AttachmentStore Attachments { get; }
    public INoteStore Store => _services.GetRequiredService<INoteStore>();
    public ITemplateStore Templates => _services.GetRequiredService<ITemplateStore>();
    public INoteSearch Search => _services.GetRequiredService<INoteSearch>();
    public NoteFactory Factory => _services.GetRequiredService<NoteFactory>();
    public NoteOperations Ops => _services.GetRequiredService<NoteOperations>();
    public NoteEvents Events => _services.GetRequiredService<NoteEvents>();
    public ICommandStore Commands => _services.GetRequiredService<ICommandStore>();

    public INoteHistory History => _services.GetRequiredService<INoteHistory>();
    public ITrash Trash => _services.GetRequiredService<ITrash>();
    public string DatabasePath => _path;
    public IServiceProvider Services => _services;

    public static async Task<TestDb> CreateAsync(TimeProvider? clock = null)
    {
        var db = new TestDb(clock);
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
