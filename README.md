# JNotes

Self-hosted, single-user notes app (Blazor Server + Tiptap + SQLite) that Claude Desktop can
work with over MCP. See [docs/SPEC.md](docs/SPEC.md) for the full design.

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
docker compose up -d
```

Then open `http://<pi-ip>:8080`. To avoid your password ending up in shell history, use
`docker compose run --rm jnotes hash-password` (interactive prompt) and paste the result into `.env`.

Alternatively, cross-build on a PC and copy the image over:

```sh
docker buildx build --platform linux/arm64 -t jnotes:latest -o type=docker,dest=jnotes.tar .
scp jnotes.tar pi@<pi-ip>:~ && ssh pi@<pi-ip> docker load -i jnotes.tar
```

All state is in the `jnotes-data` volume (`/data`). Putting Docker's data on a USB SSD rather
than the SD card is recommended.
