using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddStylosSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StylosDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StylosDevices", x => x.Id);
                    table.CheckConstraint("CK_StylosDevices_Name", "length(\"Name\") > 0 AND length(\"Name\") <= 100");
                    table.CheckConstraint("CK_StylosDevices_TokenHash", "length(\"TokenHash\") = 64");
                });

            migrationBuilder.CreateTable(
                name: "StylosFiles",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Rev = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Cursor = table.Column<long>(type: "INTEGER", nullable: false),
                    DraftUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StylosFiles", x => x.FileId);
                    table.CheckConstraint("CK_StylosFiles_Hash", "length(\"Hash\") = 64");
                    table.CheckConstraint("CK_StylosFiles_Rev", "\"Rev\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "StylosLibraryItems",
                columns: table => new
                {
                    OwnerKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cursor = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StylosLibraryItems", x => new { x.OwnerKey, x.NoteId });
                    table.CheckConstraint("CK_StylosLibraryItems_Signature", "length(\"Signature\") = 64");
                });

            migrationBuilder.CreateTable(
                name: "StylosPairings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DevicePublicKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OwnerKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TokenEncrypted = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StylosPairings", x => x.Id);
                    table.CheckConstraint("CK_StylosPairings_Status", "\"Status\" IN ('pending', 'approved', 'denied', 'expired')");
                });

            migrationBuilder.CreateTable(
                name: "StylosSyncStates",
                columns: table => new
                {
                    OwnerKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FilesCursor = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryCursor = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StylosSyncStates", x => x.OwnerKey);
                    table.CheckConstraint("CK_StylosSyncStates_FilesCursor", "\"FilesCursor\" >= 0");
                    table.CheckConstraint("CK_StylosSyncStates_LibraryCursor", "\"LibraryCursor\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_StylosDevices_OwnerKey",
                table: "StylosDevices",
                column: "OwnerKey");

            migrationBuilder.CreateIndex(
                name: "IX_StylosDevices_TokenHash",
                table: "StylosDevices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StylosFiles_OwnerKey_Cursor",
                table: "StylosFiles",
                columns: new[] { "OwnerKey", "Cursor" });

            migrationBuilder.CreateIndex(
                name: "IX_StylosFiles_OwnerKey_DraftId",
                table: "StylosFiles",
                columns: new[] { "OwnerKey", "DraftId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StylosFiles_OwnerKey_Path",
                table: "StylosFiles",
                columns: new[] { "OwnerKey", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_StylosLibraryItems_OwnerKey_Cursor",
                table: "StylosLibraryItems",
                columns: new[] { "OwnerKey", "Cursor" });

            migrationBuilder.CreateIndex(
                name: "IX_StylosPairings_Code",
                table: "StylosPairings",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_StylosPairings_Status_ExpiresAtUtc",
                table: "StylosPairings",
                columns: new[] { "Status", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StylosDevices");

            migrationBuilder.DropTable(
                name: "StylosFiles");

            migrationBuilder.DropTable(
                name: "StylosLibraryItems");

            migrationBuilder.DropTable(
                name: "StylosPairings");

            migrationBuilder.DropTable(
                name: "StylosSyncStates");
        }
    }
}
