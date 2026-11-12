using JNotes.Core;

namespace JNotes.Demo;

/// <summary>Sample folders and notes that show off the editor.</summary>
public static class DemoNotes
{
    public static async Task SeedAsync(INoteStore store)
    {
        await store.CreateNoteAsync("Welcome to JNotes", null, """
            JNotes is a self-hosted notes app. This demo runs **entirely in your browser**: the same Blazor pages, the same data code and a real SQLite database (with full-text search) compiled to WebAssembly. Change anything you like; a reload puts it all back.

            ## Things to try

            - [ ] Press **Ctrl K** and type a few letters to jump to a note
            - [ ] Type `/` on an empty line for the block menu (headings, lists, tables, images, YouTube…)
            - [ ] Drag a note onto a folder in the sidebar to move it
            - [ ] Search all notes for *risotto* with the search button in the top bar
            - [ ] Open the history button above a note after editing it, to see and restore earlier versions
            - [x] Delete a note and bring it back from **Trash** (with Undo)

            ## Asking Claude inside a note

            Type `/ai ` at the start of an empty line, write a request, and press Enter. The real app hands the note to Claude Code and drops the answer in place for you to accept or discard. Here, a stand-in shows what would be sent. There's one waiting in *Projects › Book notes*.

            > Notes are stored as Markdown, so they export cleanly to Obsidian, VS Code or any other Markdown tool.
            """);

        var home = await store.CreateFolderAsync("Home", null);
        var recipes = await store.CreateFolderAsync("Recipes", home.Id);
        var work = await store.CreateFolderAsync("Projects", null);

        await store.CreateNoteAsync("Mushroom risotto", recipes.Id, """
            Serves 4 · about 40 minutes

            | Ingredient | Amount |
            | --- | --- |
            | Arborio rice | 1½ cups |
            | Mushrooms | 1 lb, sliced |
            | Stock | 5 cups, kept hot |
            | Parmesan | ½ cup, grated |
            | Butter | 2 tbsp |

            1. Brown the mushrooms in butter; set aside.
            2. Toast the rice for 2 minutes, then add stock a ladle at a time, stirring, until creamy (about 20 minutes).
            3. Stir in the mushrooms, Parmesan and a last knob of butter. Rest 2 minutes before serving.
            """);

        await store.CreateNoteAsync("Weekend chores", home.Id, """
            - [x] Change furnace filter
            - [ ] Clean gutters
            - [ ] Return library books
            - [ ] Oil change (due at 52,000 miles)
            """);

        await store.CreateNoteAsync("Notes app roadmap", work.Id, """
            ## Done

            - Full-text search with highlighted snippets
            - Version history and a trash with undo
            - MCP server so Claude Desktop can read and write notes
            - File attachments that Claude can read (PDF, EPUB, Word)

            ## Next

            - Share a read-only link to a single note
            - Offline edits on a phone

            ```csharp
            // Every save goes through one place, so history and search stay in step.
            await notes.SaveAsync(id, markdown, expectedVersion);
            ```
            """);

        await store.CreateNoteAsync("Book notes: The Pragmatic Programmer", work.Id, """
            ## Ideas worth keeping

            - **Don't live with broken windows.** Fix small problems before they spread.
            - **DRY** is about knowledge, not just code: one authoritative place for each fact.
            - **Tracer bullets:** build a thin end-to-end slice first, then fill it in.

            :::ai-request{id="demo01"}
            Turn these ideas into three questions I could ask in a code review
            :::
            """);
    }
}
