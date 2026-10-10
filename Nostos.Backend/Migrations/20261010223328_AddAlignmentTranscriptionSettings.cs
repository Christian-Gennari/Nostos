using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAlignmentTranscriptionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlignmentTranscriptionApiKeyEncrypted",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlignmentTranscriptionApiStyle",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlignmentTranscriptionBaseUrl",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AlignmentTranscriptionEnabled",
                table: "AiProviderSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlignmentTranscriptionModel",
                table: "AiProviderSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AlignmentTranscriptionUseVoiceTranscription",
                table: "AiProviderSettings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlignmentTranscriptionApiKeyEncrypted",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "AlignmentTranscriptionApiStyle",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "AlignmentTranscriptionBaseUrl",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "AlignmentTranscriptionEnabled",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "AlignmentTranscriptionModel",
                table: "AiProviderSettings");

            migrationBuilder.DropColumn(
                name: "AlignmentTranscriptionUseVoiceTranscription",
                table: "AiProviderSettings");
        }
    }
}
