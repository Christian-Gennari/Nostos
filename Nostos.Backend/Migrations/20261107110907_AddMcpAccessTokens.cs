using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpAccessTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAccessTokens", x => x.Id);
                    table.CheckConstraint("CK_McpAccessTokens_Name", "length(\"Name\") > 0 AND length(\"Name\") <= 80");
                    table.CheckConstraint("CK_McpAccessTokens_Scope", "\"Scope\" IN ('read', 'read_write')");
                    table.CheckConstraint("CK_McpAccessTokens_TokenHash", "length(\"TokenHash\") = 64");
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpAccessTokens_TokenHash",
                table: "McpAccessTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpAccessTokens");
        }
    }
}
