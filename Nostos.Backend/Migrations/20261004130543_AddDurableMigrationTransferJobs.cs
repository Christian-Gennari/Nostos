using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableMigrationTransferJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationJobRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RecoveryStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressPhase = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressBytesProcessed = table.Column<long>(type: "INTEGER", nullable: false),
                    ProgressTotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    ProgressCompletedChunks = table.Column<int>(type: "INTEGER", nullable: true),
                    ProgressTotalChunks = table.Column<int>(type: "INTEGER", nullable: true),
                    ProgressMessage = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MigrationLeaseToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreationPayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 96, nullable: true),
                    FailureMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DestinationRevision = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PreparedStagingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PreparedImportMetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReservedStorageBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ReservationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CancellationReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationJobRecords", x => x.Id);
                    table.CheckConstraint("CK_MigrationJobRecords_AttemptNumber", "\"AttemptNumber\" >= 1");
                    table.CheckConstraint("CK_MigrationJobRecords_IdempotencyKey", "length(\"IdempotencyKey\") > 0 AND length(\"IdempotencyKey\") <= 128");
                    table.CheckConstraint("CK_MigrationJobRecords_LeaseToken", "\"MigrationLeaseToken\" IS NULL OR (length(\"MigrationLeaseToken\") > 0 AND length(\"MigrationLeaseToken\") <= 128)");
                    table.CheckConstraint("CK_MigrationJobRecords_ReservedStorageBytes", "\"ReservedStorageBytes\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "MigrationStorageReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MaterializedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationStorageReservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationExportArtifactRecords",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationExportArtifactRecords", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_MigrationExportArtifactRecords_MigrationJobRecords_JobId",
                        column: x => x.JobId,
                        principalTable: "MigrationJobRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MigrationSessionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ChunkSize = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalChunks = table.Column<int>(type: "INTEGER", nullable: false),
                    FileIdentitySizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileIdentitySha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientFingerprint = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreationPayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReceivedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationSessionRecords", x => x.Id);
                    table.CheckConstraint("CK_MigrationSessionRecords_ChunkSize", "\"ChunkSize\" >= 4194304 AND \"ChunkSize\" <= 67108864");
                    table.CheckConstraint("CK_MigrationSessionRecords_FileIdentitySize", "\"FileIdentitySizeBytes\" = \"TotalBytes\"");
                    table.CheckConstraint("CK_MigrationSessionRecords_ReceivedBytes", "\"ReceivedBytes\" >= 0 AND \"ReceivedBytes\" <= \"TotalBytes\"");
                    table.CheckConstraint("CK_MigrationSessionRecords_TotalBytes", "\"TotalBytes\" > 0");
                    table.CheckConstraint("CK_MigrationSessionRecords_TotalChunks", "\"TotalChunks\" > 0");
                    table.ForeignKey(
                        name: "FK_MigrationSessionRecords_MigrationJobRecords_JobId",
                        column: x => x.JobId,
                        principalTable: "MigrationJobRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MigrationChunkReceiptRecords",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChunkIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    OffsetBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LengthBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationChunkReceiptRecords", x => new { x.SessionId, x.ChunkIndex });
                    table.CheckConstraint("CK_MigrationChunkReceiptRecords_Bounds", "\"ChunkIndex\" >= 0 AND \"OffsetBytes\" >= 0 AND \"LengthBytes\" > 0");
                    table.ForeignKey(
                        name: "FK_MigrationChunkReceiptRecords_MigrationSessionRecords_SessionId",
                        column: x => x.SessionId,
                        principalTable: "MigrationSessionRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationExportArtifactRecords_State_ExpiresAtUtc",
                table: "MigrationExportArtifactRecords",
                columns: new[] { "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobRecords_ExpiresAtUtc",
                table: "MigrationJobRecords",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobRecords_IdempotencyKey",
                table: "MigrationJobRecords",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobRecords_State_LeaseExpiresAtUtc",
                table: "MigrationJobRecords",
                columns: new[] { "State", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationJobRecords_UpdatedAtUtc",
                table: "MigrationJobRecords",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationSessionRecords_ExpiresAtUtc",
                table: "MigrationSessionRecords",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationSessionRecords_JobId_IdempotencyKey",
                table: "MigrationSessionRecords",
                columns: new[] { "JobId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationSessionRecords_JobId_State",
                table: "MigrationSessionRecords",
                columns: new[] { "JobId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStorageReservations_ExpiresAtUtc",
                table: "MigrationStorageReservations",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationChunkReceiptRecords");

            migrationBuilder.DropTable(
                name: "MigrationExportArtifactRecords");

            migrationBuilder.DropTable(
                name: "MigrationStorageReservations");

            migrationBuilder.DropTable(
                name: "MigrationSessionRecords");

            migrationBuilder.DropTable(
                name: "MigrationJobRecords");
        }
    }
}
