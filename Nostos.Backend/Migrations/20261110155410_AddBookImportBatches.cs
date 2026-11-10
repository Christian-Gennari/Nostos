using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddBookImportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangeCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BookImportBatchBooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ChangesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBookId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReadingListEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NoteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NoteIdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookImportBatchBooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookImportBatchBooks_BookImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "BookImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookImportBatchBooks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookImportBatchBooks_BatchId",
                table: "BookImportBatchBooks",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BookImportBatchBooks_BookId",
                table: "BookImportBatchBooks",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookImportBatches_CreatedAtUtc",
                table: "BookImportBatches",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookImportBatchBooks");

            migrationBuilder.DropTable(
                name: "BookImportBatches");
        }
    }
}
