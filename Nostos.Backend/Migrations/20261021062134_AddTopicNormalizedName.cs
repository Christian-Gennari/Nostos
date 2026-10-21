using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTopicNormalizedName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Topics_Topic",
                table: "Topics");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Topics",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Every topic's key is its name folded. SQLite's upper() folds ASCII
            // only; a key that is not the full fold is set right by the merge
            // pass that runs after migrations (TopicRepository.MergeCaseVariantsAsync).
            migrationBuilder.Sql(
                """
                UPDATE "Topics" SET "NormalizedName" = upper(trim("Topic"));
                """);

            // Names that differ only in case share a key. The variant on the most
            // notes (then the first in binary order) keeps it; the others wait
            // under a key no name can have (names cannot contain |) until the
            // merge pass moves their notes onto it.
            migrationBuilder.Sql(
                """
                UPDATE "Topics"
                SET "NormalizedName" = "NormalizedName" || '|' || "Id"
                WHERE EXISTS (
                    SELECT 1 FROM "Topics" AS o
                    WHERE o."NormalizedName" = "Topics"."NormalizedName"
                      AND o."Id" <> "Topics"."Id"
                      AND (
                        (SELECT COUNT(*) FROM "NoteTopics" AS n WHERE n."TopicId" = o."Id")
                          > (SELECT COUNT(*) FROM "NoteTopics" AS n WHERE n."TopicId" = "Topics"."Id")
                        OR (
                          (SELECT COUNT(*) FROM "NoteTopics" AS n WHERE n."TopicId" = o."Id")
                            = (SELECT COUNT(*) FROM "NoteTopics" AS n WHERE n."TopicId" = "Topics"."Id")
                          AND o."Topic" < "Topics"."Topic")));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Topics_NormalizedName",
                table: "Topics",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Topics_NormalizedName",
                table: "Topics");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Topics");

            migrationBuilder.CreateIndex(
                name: "IX_Topics_Topic",
                table: "Topics",
                column: "Topic",
                unique: true);
        }
    }
}
