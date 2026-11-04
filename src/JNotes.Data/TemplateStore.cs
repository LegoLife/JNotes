using JNotes.Core;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

public sealed class TemplateStore(IDbContextFactory<JNotesDbContext> factory, TimeProvider clock) : ITemplateStore
{
    public async Task<IReadOnlyList<Template>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.Templates.AsNoTracking().ToListAsync(ct);
        return all.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Template?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<Template> CreateAsync(string name, string content = "", CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var template = new Template { Name = Names.Normalize(name, "Template"), Content = content, CreatedAt = now, UpdatedAt = now };
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct);
        return template;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = await Require(db, id, ct);
        t.Name = Names.Normalize(name, "Template");
        t.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveContentAsync(Guid id, string content, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = await Require(db, id, ct);
        if (t.Content == content) return;
        t.Content = content;
        t.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Templates.Remove(await Require(db, id, ct)); // FK sets Folder.DefaultTemplateId to NULL
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Template> Require(JNotesDbContext db, Guid id, CancellationToken ct) =>
        await db.Templates.FindAsync([id], ct) ?? throw new DomainException("Template not found.");
}
