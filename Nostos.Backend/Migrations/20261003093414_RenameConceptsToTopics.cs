using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class RenameConceptsToTopics : Migration
    {
        // EF scaffolds a rename of an entity as DropTable + CreateTable, which would
        // delete every topic and note link. This migration renames in place instead,
        // so existing rows (and every note's links) survive both directions.
        //
        // SQLite rewrites foreign keys that reference a renamed table or column, so
        // NoteTopics keeps pointing at Topics. The auto-generated PK_/FK_ constraint
        // names inside the tables keep their old text (SQLite cannot rename them
        // without a table rebuild); nothing reads them.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "Concepts", newName: "Topics");
            migrationBuilder.RenameColumn(name: "Concept", table: "Topics", newName: "Topic");

            migrationBuilder.RenameTable(name: "NoteConcepts", newName: "NoteTopics");
            migrationBuilder.RenameColumn(name: "ConceptId", table: "NoteTopics", newName: "TopicId");

            // SQLite has no RENAME INDEX; recreate each index under its new name.
            migrationBuilder.DropIndex(name: "IX_Concepts_Concept", table: "Topics");
            migrationBuilder.CreateIndex(name: "IX_Topics_Topic", table: "Topics", column: "Topic", unique: true);
            migrationBuilder.DropIndex(name: "IX_NoteConcepts_ConceptId", table: "NoteTopics");
            migrationBuilder.CreateIndex(name: "IX_NoteTopics_TopicId", table: "NoteTopics", column: "TopicId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_NoteTopics_TopicId", table: "NoteTopics");
            migrationBuilder.DropIndex(name: "IX_Topics_Topic", table: "Topics");

            migrationBuilder.RenameColumn(name: "TopicId", table: "NoteTopics", newName: "ConceptId");
            migrationBuilder.RenameTable(name: "NoteTopics", newName: "NoteConcepts");

            migrationBuilder.RenameColumn(name: "Topic", table: "Topics", newName: "Concept");
            migrationBuilder.RenameTable(name: "Topics", newName: "Concepts");

            migrationBuilder.CreateIndex(name: "IX_Concepts_Concept", table: "Concepts", column: "Concept", unique: true);
            migrationBuilder.CreateIndex(name: "IX_NoteConcepts_ConceptId", table: "NoteConcepts", column: "ConceptId");
        }
    }
}
