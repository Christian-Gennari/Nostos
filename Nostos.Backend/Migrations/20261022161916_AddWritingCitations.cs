using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddWritingCitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The notes each writing cites (issue #1162). Derived from the
            // writing's Markdown, which SQL cannot parse, so the table starts
            // empty and the startup pass that runs after migrations
            // (WritingCitationIndex.RebuildAllAsync, as TopicRepository.
            // MergeCaseVariantsAsync follows AddTopicNormalizedName) fills it.
            migrationBuilder.CreateTable(
                name: "WritingCitations",
                columns: table => new
                {
                    WritingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NoteId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WritingCitations", x => new { x.WritingId, x.NoteId });
                    table.ForeignKey(
                        name: "FK_WritingCitations_Notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WritingCitations_Writings_WritingId",
                        column: x => x.WritingId,
                        principalTable: "Writings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WritingCitations_NoteId",
                table: "WritingCitations",
                column: "NoteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WritingCitations");
        }
    }
}
