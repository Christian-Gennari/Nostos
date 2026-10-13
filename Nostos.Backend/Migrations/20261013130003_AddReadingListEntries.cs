using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddReadingListEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadingListEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Isbn = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PublishedDate = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Why = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RecommendedBy = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    NextPosition = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingListEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReadingListEntries_CreatedAt",
                table: "ReadingListEntries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingListEntries_NextPosition",
                table: "ReadingListEntries",
                column: "NextPosition");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReadingListEntries");
        }
    }
}
