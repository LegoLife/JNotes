using JNotes.Core;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

public sealed class CommandStore(IDbContextFactory<JNotesDbContext> factory, TimeProvider clock) : ICommandStore
{
    public async Task<IReadOnlyList<Command>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.Commands.AsNoTracking().ToListAsync(ct);
        return all.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Command?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Command?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name, ct);
    }

    public async Task<Command> CreateAsync(string title, string description, string body, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var normalized = Names.Normalize(title, "Command");
        var now = clock.GetUtcNow();
        var command = new Command
        {
            Name = await UniqueNameAsync(db, CommandPrompts.Slugify(normalized), null, ct),
            Title = normalized, Description = description.Trim(), Body = body.Trim(), CreatedAt = now, UpdatedAt = now,
        };
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        return command;
    }

    public async Task UpdateAsync(Guid id, string title, string description, string body, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var command = await db.Commands.FindAsync([id], ct) ?? throw new DomainException("Command not found.");
        var normalized = Names.Normalize(title, "Command");
        if (normalized != command.Title)
            command.Name = await UniqueNameAsync(db, CommandPrompts.Slugify(normalized), id, ct);
        command.Title = normalized;
        command.Description = description.Trim();
        command.Body = body.Trim();
        command.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Commands.Remove(await db.Commands.FindAsync([id], ct) ?? throw new DomainException("Command not found."));
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string> UniqueNameAsync(JNotesDbContext db, string slug, Guid? self, CancellationToken ct)
    {
        var name = slug;
        for (var i = 2; await db.Commands.AnyAsync(c => c.Name == name && c.Id != self, ct); i++) name = $"{slug}-{i}";
        return name;
    }
}
