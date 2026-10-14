using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddReadAloudProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TtsApiKeyEncrypted",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TtsBaseUrl",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TtsEnabled",
                table: "AiProviderSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TtsModel",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TtsApiKeyEncrypted",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "TtsBaseUrl",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "TtsEnabled",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "TtsModel",
                table: "AiProviderSettings");
        }
    }
}
