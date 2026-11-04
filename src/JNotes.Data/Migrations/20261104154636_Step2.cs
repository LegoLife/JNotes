using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JNotes.Data.Migrations
{
    /// <inheritdoc />
    public partial class Step2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultTemplateId",
                table: "Folders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Templates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_DefaultTemplateId",
                table: "Folders",
                column: "DefaultTemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Templates_DefaultTemplateId",
                table: "Folders",
                column: "DefaultTemplateId",
                principalTable: "Templates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ---- Full-text search: standalone FTS5 table kept in sync by triggers ----
            // (Notes has a GUID key, so an external-content table keyed on rowid would be fragile.)
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE NotesFts USING fts5(
                    NoteId UNINDEXED, Title, Content,
                    tokenize = 'unicode61 remove_diacritics 2',
                    prefix = '2 3'
                );
                """);
            migrationBuilder.Sql("INSERT INTO NotesFts (NoteId, Title, Content) SELECT Id, Title, Content FROM Notes;");
            migrationBuilder.Sql("""
                CREATE TRIGGER Notes_fts_insert AFTER INSERT ON Notes BEGIN
                    INSERT INTO NotesFts (NoteId, Title, Content) VALUES (new.Id, new.Title, new.Content);
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER Notes_fts_delete AFTER DELETE ON Notes BEGIN
                    DELETE FROM NotesFts WHERE NoteId = old.Id;
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER Notes_fts_update AFTER UPDATE OF Title, Content ON Notes BEGIN
                    UPDATE NotesFts SET Title = new.Title, Content = new.Content WHERE NoteId = old.Id;
                END;
                """);

            // ---- Starter templates ----
            var seeded = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero).UtcTicks;
            migrationBuilder.InsertData(
                table: "Templates",
                columns: ["Id", "Name", "Content", "CreatedAt", "UpdatedAt"],
                values: new object[,]
                {
                    {
                        new Guid("6f1d2a43-8b0e-4c51-9a57-2b8f0c3d1e01"), "Book",
                        "**Author:** \n\n**Started:** {{date}}\n\n## Why I'm reading it\n\n## Key ideas\n\n## Favourite quotes\n",
                        seeded, seeded
                    },
                    {
                        new Guid("6f1d2a43-8b0e-4c51-9a57-2b8f0c3d1e02"), "Book chapter",
                        "**Book:** {{folder}} · **Read:** {{date}}\n\n## My highlights\n\n## My notes\n\n## Summary\n",
                        seeded, seeded
                    },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Notes_fts_insert;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Notes_fts_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Notes_fts_update;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS NotesFts;");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Templates_DefaultTemplateId",
                table: "Folders");

            migrationBuilder.DropTable(
                name: "Templates");

            migrationBuilder.DropIndex(
                name: "IX_Folders_DefaultTemplateId",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "DefaultTemplateId",
                table: "Folders");
        }
    }
}
