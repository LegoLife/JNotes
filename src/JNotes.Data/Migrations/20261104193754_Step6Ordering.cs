using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JNotes.Data.Migrations
{
    /// <inheritdoc />
    public partial class Step6Ordering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Notes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Folders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Folders");
        }
    }
}
