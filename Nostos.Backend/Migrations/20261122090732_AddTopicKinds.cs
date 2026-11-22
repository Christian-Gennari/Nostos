using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTopicKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "KindId",
                table: "Topics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KindSource",
                table: "Topics",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TopicKinds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ColorKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopicKinds", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TopicKinds",
                columns: new[] { "Id", "ColorKey", "CreatedAt", "Name", "NormalizedName", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("11111111-0000-0000-0000-000000000001"), "kind-1", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Person", "PERSON", 1 },
                    { new Guid("11111111-0000-0000-0000-000000000002"), "kind-2", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Idea", "IDEA", 2 },
                    { new Guid("11111111-0000-0000-0000-000000000003"), "kind-3", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Place", "PLACE", 3 },
                    { new Guid("11111111-0000-0000-0000-000000000004"), "kind-4", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Work", "WORK", 4 },
                    { new Guid("11111111-0000-0000-0000-000000000005"), "kind-5", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Event", "EVENT", 5 },
                    { new Guid("11111111-0000-0000-0000-000000000006"), "kind-6", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Field", "FIELD", 6 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Topics_KindId",
                table: "Topics",
                column: "KindId");

            migrationBuilder.CreateIndex(
                name: "IX_TopicKinds_ColorKey",
                table: "TopicKinds",
                column: "ColorKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TopicKinds_NormalizedName",
                table: "TopicKinds",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Topics_TopicKinds_KindId",
                table: "Topics",
                column: "KindId",
                principalTable: "TopicKinds",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Topics_TopicKinds_KindId",
                table: "Topics");

            migrationBuilder.DropTable(
                name: "TopicKinds");

            migrationBuilder.DropIndex(
                name: "IX_Topics_KindId",
                table: "Topics");

            migrationBuilder.DropColumn(
                name: "KindId",
                table: "Topics");

            migrationBuilder.DropColumn(
                name: "KindSource",
                table: "Topics");
        }
    }
}
