using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #1404. Nullable and not backfilled on purpose: a note with no
            // UpdatedAt is versioned by its CreatedAt until its first edit.
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Notes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to backfill: the column only versions edits; it holds no
            // library content that an older schema stores elsewhere.
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Notes");
        }
    }
}
