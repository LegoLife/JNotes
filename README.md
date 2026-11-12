# JNotes

Self-hosted, single-user notes app (Blazor Server + Tiptap + SQLite) that Claude Desktop can
work with over MCP. See [docs/SPEC.md](docs/SPEC.md) for the full design.

## Live demo

**[legolife.github.io/JNotes](https://legolife.github.io/JNotes/)**: the real UI, editor and data layer running entirely in your browser
(Blazor WebAssembly, with SQLite and its FTS5 search compiled to WebAssembly) on sample notes that reset when you reload.
Pasted images and files stay in the tab. Inline `/ai` returns a canned reply instead of calling Claude, and the server-only pages
(Claude/MCP setup, Docmost import, Settings with backups and export) aren't included. The demo project is `src/JNotes.Demo`;
building it needs `dotnet workload install wasm-tools` and the editor bundle (`npm ci && npm run build` in `src/JNotes.Web/Editor`).

## Using it

- **Ctrl+K**: jump to a note by title. The search icon opens full-text search (every word must match as a word start; use `"quotes"` for exact phrases).
- **Templates** (app bar): use `{{title}}`, `{{date}}` and `{{folder}}`. Set a folder's default template from its "…" menu.
- **Images**: paste, drag in, or use the toolbar button (PNG/JPEG/GIF/WebP, ≤20 MB). **YouTube**: paste a link, or use the toolbar button.
- **Ask Claude inline**: in a note type `/ai ` + what you want and press Enter; the answer appears in place a few seconds later (Accept/Discard). JNotes runs Claude Code headless with your Claude subscription — no API key. In Docker, set `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`).
- **Claude** (✨ in the app bar): a "Test it" button for inline answers, Claude Desktop connection instructions, and your editable commands for bigger jobs in Claude Desktop.
- **History** (clock icon on a note): earlier versions — saved before every Claude edit and every ~10 minutes while you type, kept 30 days — with one-click restore.
- **Trash** (sidebar): deleting is undoable; items are kept 30 days.
- **Backups** (Settings): nightly database snapshot at 3am (last 14 kept in `/data/backups`). Optional hourly copy to Google Drive via the `rclone` service — setup steps are on the Settings page; start it with `docker compose --profile backup up -d`.
- **Export**: Settings → *Download all notes* gives a zip of Markdown files in their folders with images (opens in Obsidian/VS Code, and re-imports into JNotes). Each note also has a download button.
- **Import from Docmost** (app bar): upload a space export (Markdown + attachments). Any content that couldn't be converted is listed after the import.

## Develop

Requires .NET 10 SDK and Node 20+.

```sh
dotnet run --project src/JNotes.Web --launch-profile http   # http://localhost:5025, password: dev
dotnet test
```

The Tiptap bundle (`src/JNotes.Web/Editor`) builds automatically on first build. After editing
`Editor/src/*.js`, run `npm run build` in that folder.

Data (SQLite DB + cookie keys) lives in `src/JNotes.Web/data/` in development, `/data` in Docker.

Add a migration:

```sh
JNotes__PasswordHash=x dotnet ef migrations add <Name> --project src/JNotes.Data --startup-project src/JNotes.Web
```

## Deploy on the Raspberry Pi (64-bit Pi OS)

```sh
git clone <repo> jnotes && cd jnotes
docker compose build
echo "JNOTES_PASSWORD_HASH=$(docker compose run --rm -T jnotes hash-password 'your-password')" > .env
echo "JNOTES_MCP_TOKEN=$(docker compose run --rm -T jnotes generate-token)" >> .env
# On a PC with Claude Code: run `claude setup-token`, then add the result:
echo "CLAUDE_CODE_OAUTH_TOKEN=<token from claude setup-token>" >> .env
docker compose up -d
```

Then open `http://<pi-ip>:8080` and follow the ✨ Claude page to connect Claude Desktop (dev token: `dev-mcp-token`). To avoid your password ending up in shell history, use
`docker compose run --rm jnotes hash-password` (interactive prompt) and paste the result into `.env`.

Alternatively, cross-build on a PC and copy the image over:

```sh
docker buildx build --platform linux/arm64 -t jnotes:latest -o type=docker,dest=jnotes.tar .
scp jnotes.tar pi@<pi-ip>:~ && ssh pi@<pi-ip> docker load -i jnotes.tar
```

All state is in the `jnotes-data` volume (`/data`). Putting Docker's data on a USB SSD rather
than the SD card is recommended.
