using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOAuthGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpOAuthGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ClientName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOAuthGrants", x => x.Id);
                    table.CheckConstraint("CK_McpOAuthGrants_Scope", "\"Scope\" IN ('read', 'read_write')");
                });

            migrationBuilder.CreateTable(
                name: "McpOAuthTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOAuthTokens", x => x.Id);
                    table.CheckConstraint("CK_McpOAuthTokens_Kind", "\"Kind\" IN ('access', 'refresh')");
                    table.CheckConstraint("CK_McpOAuthTokens_TokenHash", "length(\"TokenHash\") = 64");
                    table.ForeignKey(
                        name: "FK_McpOAuthTokens_McpOAuthGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "McpOAuthGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthTokens_GrantId",
                table: "McpOAuthTokens",
                column: "GrantId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthTokens_TokenHash",
                table: "McpOAuthTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpOAuthTokens");

            migrationBuilder.DropTable(
                name: "McpOAuthGrants");
        }
    }
}
