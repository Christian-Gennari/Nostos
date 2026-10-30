using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <summary>
    /// Moves what a reader says about a BOOK (rating, favourite, review) from each
    /// edition (<c>Books.Progress_*</c>) onto its work (<c>Works</c>), so every
    /// edition of one book shows the same values. <c>Books.Progress_Rating</c> and
    /// <c>Progress_PersonalReview</c> stay, now meaning this edition's OWN
    /// exception (0 / null = none); <c>Progress_IsFavorite</c> is dropped, because
    /// the favourite has no exception.
    ///
    /// The roll-up is lossless and is the SQL form of <c>BookValueRollUp</c> (the C#
    /// rule that "link as one book" and old-archive import use; a test replays both
    /// over the same rows):
    /// - values agree, or only one edition has one: it becomes the book's;
    /// - values differ: the edition read most recently wins (last read first,
    ///   never read last, then newest, then lowest id), and the others keep
    ///   their own value as an edition exception;
    /// - favourite: on if any edition has it on.
    /// An edition value equal to the book's is then cleared.
    ///
    /// DESTRUCTIVE (it drops a column), so a guard refuses to run when any
    /// favourite, rating or review would not have reached the work. Statements are
    /// plain SQL that is valid on SQLite and PostgreSQL alike.
    /// </summary>
    public partial class MoveBookValuesToWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFavorite",
                table: "Works",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PersonalReview",
                table: "Works",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Works",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Roll the editions' values up onto their work. "Most recently read"
            // is the ORDER BY: edition read last first, never read last, then the
            // newest, then the lowest id so the result is deterministic.
            migrationBuilder.Sql(
                """
                UPDATE "Works" SET "Rating" = COALESCE((
                    SELECT b."Progress_Rating" FROM "Books" b
                    WHERE b."WorkId" = "Works"."Id" AND b."Progress_Rating" > 0
                    ORDER BY CASE WHEN b."Progress_LastReadAt" IS NULL THEN 1 ELSE 0 END,
                             b."Progress_LastReadAt" DESC, b."CreatedAt" DESC, b."Id"
                    LIMIT 1), 0);
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Works" SET "PersonalReview" = (
                    SELECT b."Progress_PersonalReview" FROM "Books" b
                    WHERE b."WorkId" = "Works"."Id"
                      AND b."Progress_PersonalReview" IS NOT NULL AND b."Progress_PersonalReview" <> ''
                    ORDER BY CASE WHEN b."Progress_LastReadAt" IS NULL THEN 1 ELSE 0 END,
                             b."Progress_LastReadAt" DESC, b."CreatedAt" DESC, b."Id"
                    LIMIT 1);
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Works" SET "IsFavorite" = CASE WHEN EXISTS (
                    SELECT 1 FROM "Books" b
                    WHERE b."WorkId" = "Works"."Id" AND b."Progress_IsFavorite" = TRUE)
                    THEN TRUE ELSE FALSE END;
                """);

            // Invariant guard, before anything is cleared or dropped.
            //
            // SQLite cannot RAISE from a plain statement (RAISE is trigger-only),
            // so this uses the standard abort idiom: a table whose CHECK constraint
            // is violated exactly when the invariant is broken. The INSERT...SELECT
            // inserts nothing when every favourite, rating and review has reached
            // its work, and the migration aborts with a readable error when not.
            migrationBuilder.Sql(
                """
                CREATE TABLE "_book_values_guard" (
                    violation INTEGER NOT NULL CHECK (violation = 0)
                );

                INSERT INTO "_book_values_guard" (violation)
                SELECT 1
                WHERE EXISTS (
                    SELECT 1 FROM "Books" b
                    WHERE (b."Progress_IsFavorite" = TRUE
                           OR b."Progress_Rating" > 0
                           OR (b."Progress_PersonalReview" IS NOT NULL AND b."Progress_PersonalReview" <> ''))
                      AND NOT EXISTS (
                          SELECT 1 FROM "Works" w
                          WHERE w."Id" = b."WorkId"
                            AND (b."Progress_IsFavorite" = FALSE OR w."IsFavorite" = TRUE)
                            AND (b."Progress_Rating" = 0 OR w."Rating" > 0)
                            AND (b."Progress_PersonalReview" IS NULL OR b."Progress_PersonalReview" = ''
                                 OR w."PersonalReview" IS NOT NULL)
                      )
                );

                DROP TABLE "_book_values_guard";
                """);

            // An edition value that says what the book now says is no exception.
            migrationBuilder.Sql(
                """
                UPDATE "Books" SET "Progress_Rating" = 0
                WHERE "Progress_Rating" > 0
                  AND "Progress_Rating" = (SELECT w."Rating" FROM "Works" w WHERE w."Id" = "Books"."WorkId");
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Books" SET "Progress_PersonalReview" = NULL
                WHERE "Progress_PersonalReview" IS NOT NULL
                  AND ("Progress_PersonalReview" = ''
                       OR "Progress_PersonalReview" = (SELECT w."PersonalReview" FROM "Works" w WHERE w."Id" = "Books"."WorkId"));
                """);

            migrationBuilder.DropColumn(
                name: "Progress_IsFavorite",
                table: "Books");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Progress_IsFavorite",
                table: "Books",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Restore the per-edition shape: every edition gets the book's
            // favourite, and its own rating / review when it has one, else the
            // book's. A bare AddColumn would silently lose every favourite.
            migrationBuilder.Sql(
                """
                UPDATE "Books" SET "Progress_IsFavorite" = COALESCE(
                    (SELECT w."IsFavorite" FROM "Works" w WHERE w."Id" = "Books"."WorkId"), FALSE);
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Books" SET "Progress_Rating" = COALESCE(
                    (SELECT w."Rating" FROM "Works" w WHERE w."Id" = "Books"."WorkId"), 0)
                WHERE "Progress_Rating" = 0;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Books" SET "Progress_PersonalReview" =
                    (SELECT w."PersonalReview" FROM "Works" w WHERE w."Id" = "Books"."WorkId")
                WHERE "Progress_PersonalReview" IS NULL OR "Progress_PersonalReview" = '';
                """);

            migrationBuilder.DropColumn(
                name: "IsFavorite",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "PersonalReview",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Works");
        }
    }
}
