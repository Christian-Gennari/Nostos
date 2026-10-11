using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAlignmentAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlignmentAnchors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AudioBookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TextBookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AudioRevision = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TextRevision = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AudioPositionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    TextGlobalOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    SpineIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourceHref = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Cfi = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    TextOffset = table.Column<int>(type: "INTEGER", nullable: true),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlignmentAnchors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlignmentAnchors_Books_AudioBookId",
                        column: x => x.AudioBookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlignmentAnchors_Books_TextBookId",
                        column: x => x.TextBookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlignmentAnchors_Pair_AudioPosition",
                table: "AlignmentAnchors",
                columns: new[] { "AudioBookId", "TextBookId", "AudioRevision", "TextRevision", "AudioPositionMs" });

            migrationBuilder.CreateIndex(
                name: "IX_AlignmentAnchors_Pair_TextOffset",
                table: "AlignmentAnchors",
                columns: new[] { "AudioBookId", "TextBookId", "AudioRevision", "TextRevision", "TextGlobalOffset" });

            migrationBuilder.CreateIndex(
                name: "IX_AlignmentAnchors_TextBookId",
                table: "AlignmentAnchors",
                column: "TextBookId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlignmentAnchors");
        }
    }
}
