using System.IO.Compression;
using System.Text.Json;
using JNotes.Core;
using JNotes.Data;

namespace JNotes.Web.Cli;

/// <summary>
/// <c>dotnet JNotes.Web.dll import-docmost-db &lt;dir&gt; [--dry-run]</c>
/// <para>
/// Imports a dump of Docmost's database: <c>&lt;dir&gt;/pages.json</c> and <c>attachments.json</c> (from psql)
/// plus <c>&lt;dir&gt;/storage</c> (Docmost's file storage). The dump is converted to a Docmost-style zip
/// (kept as <c>&lt;dir&gt;/docmost-converted.zip</c> for inspection) and imported with the regular importer.
/// A JNotes backup is taken first.
/// </para>
/// </summary>
public static class DocmostDbImportCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(IServiceProvider services, string dir, bool dryRun)
    {
        var pages = JsonSerializer.Deserialize<List<DocmostPage>>(await File.ReadAllTextAsync(Path.Combine(dir, "pages.json")), Json) ?? [];
        var attachmentsFile = Path.Combine(dir, "attachments.json");
        var attachments = File.Exists(attachmentsFile)
            ? JsonSerializer.Deserialize<List<DocmostAttachment>>(await File.ReadAllTextAsync(attachmentsFile), Json) ?? []
            : [];
        var storage = Path.GetFullPath(Path.Combine(dir, "storage"));
        Console.WriteLine($"Read {pages.Count} pages in {pages.Select(p => p.Space).Distinct().Count()} spaces and {attachments.Count} attachments.");

        var zipPath = Path.Combine(dir, "docmost-converted.zip");
        DocmostDbConverter.Result converted;
        await using (var zipFile = File.Create(zipPath))
        {
            converted = await DocmostDbConverter.WriteZipAsync(pages, attachments, relative =>
            {
                var full = Path.GetFullPath(Path.Combine(storage, relative));
                return full.StartsWith(storage, StringComparison.Ordinal) && File.Exists(full) ? File.OpenRead(full) : null;
            }, zipFile);
        }
        Console.WriteLine($"Converted {converted.Pages} pages and {converted.Images} images to {zipPath}.");
        foreach (var w in converted.Warnings.Distinct()) Console.WriteLine($"  ! {w}");

        if (dryRun)
        {
            Console.WriteLine("Dry run: nothing was imported.");
            return 0;
        }

        var backup = await services.GetRequiredService<BackupService>().BackupNowAsync();
        Console.WriteLine($"Backed up JNotes first: {backup}");

        using var zip = ZipFile.OpenRead(zipPath);
        var report = await services.GetRequiredService<DocmostImporter>().ImportAsync(zip, null);
        Console.WriteLine($"Imported {report.Notes} notes, {report.Folders} folders and {report.Images} images.");
        foreach (var w in report.Warnings) Console.WriteLine($"  ! {w}");
        return 0;
    }
}
