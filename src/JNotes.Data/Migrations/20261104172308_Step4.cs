using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JNotes.Data.Migrations
{
    /// <inheritdoc />
    public partial class Step4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Notes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TrashBatch",
                table: "Notes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Folders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TrashBatch",
                table: "Folders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NoteVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteVersions_Notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_TrashBatch",
                table: "Notes",
                column: "TrashBatch");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_TrashBatch",
                table: "Folders",
                column: "TrashBatch");

            migrationBuilder.CreateIndex(
                name: "IX_NoteVersions_NoteId_CreatedAt",
                table: "NoteVersions",
                columns: new[] { "NoteId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteVersions");

            migrationBuilder.DropIndex(
                name: "IX_Notes_TrashBatch",
                table: "Notes");

            migrationBuilder.DropIndex(
                name: "IX_Folders_TrashBatch",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "TrashBatch",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "TrashBatch",
                table: "Folders");
        }
    }
}
