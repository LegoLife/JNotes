# JNotes — Specification

Self-hosted, single-user notes app. Runs in Docker on a Raspberry Pi 5 (4GB, `linux/arm64`).
Claude Desktop (on the LAN, 192.168.1.2) connects to it as an **MCP server**, using the
user's Claude subscription — no LLM API keys.

## Stack
- .NET 10, **Blazor only**, Interactive Server render mode. UI: **MudBlazor**.
- Editor: **Tiptap** (v3) wrapped in a Blazor component via a thin JS interop layer
  (`src/JNotes.Web/Editor`, bundled with esbuild into `wwwroot/js/editor.js`).
- Storage: **SQLite** via EF Core (migrations applied at startup); full-text search via **FTS5**.
- Document content is stored as **Markdown** — the single source of truth. All
  markdown <-> editor conversion happens in Tiptap (`@tiptap/markdown`).
- Solution: `JNotes.Web` (UI, MCP endpoint, interop), `JNotes.Core` (domain),
  `JNotes.Data` (EF Core/SQLite), `JNotes.Tests` (xUnit, real SQLite files).
- Deployment: one `linux/arm64` image + `docker-compose.yml` (app + rclone sidecar).
  All state under `/data` (USB SSD recommended over SD card).

## Notes & organisation
- **Strict folders**: folders contain folders and documents; documents are leaves.
  Documents may also live at the root.
- WYSIWYG markdown editing, autosave (~1s debounce).
- **YouTube**: paste a URL → embedded player; stored in markdown as a bare URL on its own line.
- **Images**: paste/drag → stored in `/data/attachments`, served by the app. Other file types out of scope.
- **Templates**: variables `{{title}}`, `{{date}}`, `{{folder}}`; a folder can set a default
  template; templates may contain pending AI blocks.
- **Search**: Ctrl+K title quick-switcher; full-text search page (FTS5) with highlighted
  snippets, folder filter, relevance ranking. Same engine backs the MCP `search_notes` tool.
- **Trash**: deletes are soft, kept 30 days.
- **Version history**: snapshot before every MCP write and at most every 10 minutes while
  editing; kept 30 days; restore from a history view.

## Auth
- Web UI: single user; password hash supplied via config (`JNotes__PasswordHash`,
  generated with `dotnet JNotes.Web.dll hash-password`); cookie valid 30 days.
  Data-protection keys persisted to `/data/keys`.
- App is proxy-agnostic: honours `X-Forwarded-*`; exposure/tunnelling handled by the user.

## Claude integration (MCP)
- MCP endpoint `/mcp` in the same app (official C# SDK `ModelContextProtocol.AspNetCore`,
  Streamable HTTP). **LAN only**, **static bearer token**; Claude Desktop connects through
  `mcp-remote`. Auth check sits behind an interface so OAuth can be added later.
  Pi should have a DHCP reservation.
- Tools — read: `list_folders`, `search_notes`, `read_note`, `list_templates`,
  `list_pending_requests`; write: `create_note` (optionally from template),
  `append_to_note`, `replace_section` (by heading), `fulfill_request`, `move_note`.
  **No delete, no full-document overwrite.**
- **Commands** = MCP prompts, editable in-app (e.g. Chapter Summary), surfaced in Claude's "+" menu.
- **Inline requests**: typing `/ai …` inserts a pending block (markdown: `:::ai …:::`).
  A "process my requests" prompt makes Claude fetch pending blocks with context and fill them.
- Fulfilled requests render **live** in an open editor (targeted patch, cursor undisturbed)
  as an **AI-generated callout** with Accept (→ plain text) / Discard.
- Prompts instruct Claude to ground output in the user's notes first, use its own knowledge
  only when confident, and flag uncertainty. Book Chapter template has a "My highlights" section.

## Import / export
- **Docmost import**: upload Docmost's Markdown zip export, pick a target folder. Pages with
  children become folders (`Dune/Dune.md`); images imported to attachments; report of anything unconverted.
- **Export**: whole vault as zip (folder tree of `.md` + `attachments/`), or single note as `.md`.

## Backups
- Nightly `VACUUM INTO` DB copy + attachments into `/data/backups`; keep last 14 locally.
- rclone sidecar copies to Google Drive (`JNotes Backups`), pruning Drive copies older than 7 days.
- Settings shows last-backup time; warns if older than 36h.

## Build order
1. Core notes: folders, documents, editor, markdown storage, autosave, login, Docker on Pi
2. Search, templates, YouTube embeds, images, basic Docmost import
3. MCP server, prompts, `/ai` request blocks + AI callout
4. History, trash, backups (incl. Drive)
5. Full Docmost import + export
