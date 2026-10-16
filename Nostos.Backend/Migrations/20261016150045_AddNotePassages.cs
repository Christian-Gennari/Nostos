using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddNotePassages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Notes",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "note");

            migrationBuilder.AddColumn<string>(
                name: "PassageEnd",
                table: "Notes",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PassageHtml",
                table: "Notes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PassageTitle",
                table: "Notes",
                type: "TEXT",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "PassageEnd",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "PassageHtml",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "PassageTitle",
                table: "Notes");
        }
    }
}
