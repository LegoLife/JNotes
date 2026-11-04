using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JNotes.Data.Migrations
{
    /// <inheritdoc />
    public partial class Step3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_Name",
                table: "Commands",
                column: "Name",
                unique: true);

            // ---- Starter commands (MCP prompts) ----
            var seeded = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero).UtcTicks;
            migrationBuilder.InsertData(
                table: "Commands",
                columns: ["Id", "Name", "Title", "Description", "Body", "CreatedAt", "UpdatedAt"],
                values: new object[,]
                {
                    {
                        new Guid("8a3c1f20-5d4e-4b7a-9c61-0e2f3a4b5c01"), "process-requests", "Process my AI requests",
                        "Fill in every pending ✨ AI request block in my notes.",
                        "Use list_pending_requests to find the AI requests I've left in my JNotes notes. For each one, read_note to " +
                        "understand its context (the surrounding section and the rest of the note), write what it asks for, and " +
                        "fill it in with fulfill_request using that request's id. Handle all of them, then list what you did.",
                        seeded, seeded
                    },
                    {
                        new Guid("8a3c1f20-5d4e-4b7a-9c61-0e2f3a4b5c02"), "chapter-summary", "Chapter summary",
                        "Summarise a book chapter into my chapter note.",
                        "Write a summary of chapter {{chapter}} of {{book}}.\n\n" +
                        "1. Find my notes for this book and chapter with search_notes / read_note (highlights and notes).\n" +
                        "2. Write a clear summary (key events, ideas and how it connects to earlier chapters), built from my highlights and notes.\n" +
                        "3. Put it under the “Summary” heading of my note for that chapter using replace_section. If there is no note for the " +
                        "chapter yet, create one with create_note using the “Book chapter” template in the book's folder.",
                        seeded, seeded
                    },
                    {
                        new Guid("8a3c1f20-5d4e-4b7a-9c61-0e2f3a4b5c03"), "quiz-me", "Quiz me",
                        "Make review questions from one of my notes.",
                        "Read my note “{{note}}” and write 5 short review questions (with answers in a collapsible list underneath) " +
                        "that test the most important ideas in it. Add them to the end of the note with append_to_note under a “Review questions” heading.",
                        seeded, seeded
                    },
                });

            // Give the "Book chapter" starter template an AI summary request, unless the user has edited it.
            migrationBuilder.Sql("""
                UPDATE Templates
                SET Content = '**Book:** {{folder}} · **Read:** {{date}}' || char(10) || char(10) ||
                              '## My highlights' || char(10) || char(10) ||
                              '## My notes' || char(10) || char(10) ||
                              '## Summary' || char(10) || char(10) ||
                              ':::ai-request{id="summary"}' || char(10) ||
                              'Summarise this chapter from my highlights and notes above' || char(10) || ':::' || char(10)
                WHERE Id = '6F1D2A43-8B0E-4C51-9A57-2B8F0C3D1E02'
                  AND Content = '**Book:** {{folder}} · **Read:** {{date}}' || char(10) || char(10) ||
                                '## My highlights' || char(10) || char(10) ||
                                '## My notes' || char(10) || char(10) ||
                                '## Summary' || char(10);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commands");
        }
    }
}
