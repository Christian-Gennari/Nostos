using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddBookUploadSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookUploadSessionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ChunkSize = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalChunks = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ClientContentType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    FileSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientFingerprint = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ReceivedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActiveSessionKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookUploadSessionRecords", x => x.Id);
                    table.CheckConstraint("CK_BookUploadSessionRecords_Bounds", "\"TotalBytes\" > 0 AND \"ReceivedBytes\" >= 0 AND \"ReceivedBytes\" <= \"TotalBytes\" AND \"ChunkSize\" > 0 AND \"TotalChunks\" > 0");
                    table.CheckConstraint("CK_BookUploadSessionRecords_State", "\"State\" >= 0 AND \"State\" <= 7");
                    table.ForeignKey(
                        name: "FK_BookUploadSessionRecords_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookUploadPartReceiptRecords",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PartIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    OffsetBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LengthBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookUploadPartReceiptRecords", x => new { x.SessionId, x.PartIndex });
                    table.CheckConstraint("CK_BookUploadPartReceiptRecords_Bounds", "\"PartIndex\" >= 0 AND \"OffsetBytes\" >= 0 AND \"LengthBytes\" > 0");
                    table.ForeignKey(
                        name: "FK_BookUploadPartReceiptRecords_BookUploadSessionRecords_SessionId",
                        column: x => x.SessionId,
                        principalTable: "BookUploadSessionRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookUploadSessionRecords_ActiveSessionKey",
                table: "BookUploadSessionRecords",
                column: "ActiveSessionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookUploadSessionRecords_BookId",
                table: "BookUploadSessionRecords",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookUploadSessionRecords_ExpiresAtUtc",
                table: "BookUploadSessionRecords",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BookUploadSessionRecords_OwnerKey_BookId",
                table: "BookUploadSessionRecords",
                columns: new[] { "OwnerKey", "BookId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookUploadPartReceiptRecords");

            migrationBuilder.DropTable(
                name: "BookUploadSessionRecords");
        }
    }
}
