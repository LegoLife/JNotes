using JNotes.Core;

namespace JNotes.Tests;

public class TemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Renders_known_variables_and_leaves_unknown_ones()
    {
        var result = TemplateRenderer.Render("# {{title}} ({{ Folder }}) {{date}} {{nope}}", "Ch 3", "Dune", Now);
        Assert.Equal("# Ch 3 (Dune) 2026-11-04 {{nope}}", result);
    }

    [Fact]
    public void Folder_variable_is_empty_at_top_level() =>
        Assert.Equal("[]", TemplateRenderer.Render("[{{folder}}]", "x", null, Now));

    [Fact]
    public async Task Starter_templates_are_seeded()
    {
        await using var db = await TestDb.CreateAsync();
        var names = (await db.Templates.ListAsync()).Select(t => t.Name);
        Assert.Equal(["Book", "Book chapter"], names);
    }

    [Fact]
    public async Task Factory_creates_note_with_rendered_template()
    {
        await using var db = await TestDb.CreateAsync();
        var dune = await db.Store.CreateFolderAsync("Dune", null);
        var template = await db.Templates.CreateAsync("Chapter", "# {{title}}\n\nFrom {{folder}}");

        var note = await db.Factory.CreateAsync("Chapter 3", dune.Id, template.Id);

        Assert.Equal("# Chapter 3\n\nFrom Dune", (await db.Store.GetNoteAsync(note.Id))!.Content);
    }

    [Fact]
    public async Task Deleting_a_template_clears_folder_defaults()
    {
        await using var db = await TestDb.CreateAsync();
        var folder = await db.Store.CreateFolderAsync("Books", null);
        var template = await db.Templates.CreateAsync("T");
        await db.Store.SetDefaultTemplateAsync(folder.Id, template.Id);
        Assert.Equal(template.Id, (await db.Store.GetFolderAsync(folder.Id))!.DefaultTemplateId);

        await db.Templates.DeleteAsync(template.Id);

        Assert.Null((await db.Store.GetFolderAsync(folder.Id))!.DefaultTemplateId);
    }

    [Fact]
    public async Task Default_template_must_exist()
    {
        await using var db = await TestDb.CreateAsync();
        var folder = await db.Store.CreateFolderAsync("Books", null);
        await Assert.ThrowsAsync<DomainException>(() => db.Store.SetDefaultTemplateAsync(folder.Id, Guid.NewGuid()));
    }
}
