using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteImportTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NoteImportBookLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteImportBookLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteImportBookLinks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteImportBatchNotes",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteImportBatchNotes", x => new { x.BatchId, x.NoteId });
                    table.ForeignKey(
                        name: "FK_NoteImportBatchNotes_NoteImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "NoteImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteImportBatchNotes_Notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteImportBatches_CreatedAtUtc",
                table: "NoteImportBatches",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NoteImportBatchNotes_NoteId",
                table: "NoteImportBatchNotes",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteImportBookLinks_BookId",
                table: "NoteImportBookLinks",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteImportBookLinks_Source_SourceKey",
                table: "NoteImportBookLinks",
                columns: new[] { "Source", "SourceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteImportBatchNotes");

            migrationBuilder.DropTable(
                name: "NoteImportBookLinks");

            migrationBuilder.DropTable(
                name: "NoteImportBatches");
        }
    }
}
