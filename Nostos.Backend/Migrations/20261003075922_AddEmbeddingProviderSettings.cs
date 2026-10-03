using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingApiKeyEncrypted",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingBaseUrl",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmbeddingEnabled",
                table: "AiProviderSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingApiKeyEncrypted",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "EmbeddingBaseUrl",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "EmbeddingEnabled",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "AiProviderSettings");
        }
    }
}
