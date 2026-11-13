using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <summary>
    /// Gives back the digital type to books that are one.
    ///
    /// Older builds defaulted Add Book to "Physical book" and let any row take
    /// an upload, so a book added by ISBN and then given a file ended up with
    /// <c>BookType = 'physical'</c> AND <c>FileDetails_HasFile = 1</c> — a shape
    /// the product stopped making in #332, where a physical book became
    /// metadata-only. Those rows show paper controls next to a working Read
    /// button, are labelled "Physical" in the library, and keep their page count
    /// and ISBN in the physical columns.
    ///
    /// Every such row is converted in place, by the file it already holds, using
    /// <see cref="Nostos.Backend.Services.BookAssetFormats"/>'s extension sets:
    /// - an ebook extension becomes <c>ebook</c>, and the physical columns
    ///   (<c>PhysicalBookModel_Isbn</c>, <c>PhysicalBookModel_PageCount</c>) move
    ///   into the ebook ones;
    /// - an audio extension becomes <c>audiobook</c>. An audiobook has no ISBN or
    ///   page count of its own, so those two values are cleared (they describe
    ///   the print edition) and <c>NormalizedIsbn</c> with them: an audiobook
    ///   never carries ISBN identity;
    /// - a row read in the paper format (<c>{"page":N}</c>) keeps its place in
    ///   the paper reader and stays <c>physical</c>; so does a row whose file
    ///   name has no book extension this product accepts. That is a guess this
    ///   migration does not make.
    ///
    /// Nothing is split into physical + digital editions: the "physical" label
    /// came from the Add Book default, not from anyone saying they own a paper
    /// copy.
    ///
    /// Book ids, reading progress, notes, ratings, quotes and collection
    /// membership are untouched; only the discriminator, the two physical
    /// columns and an audiobook's ISBN identity change. Running it twice is a
    /// no-op: after the first pass no physical row has a file (but a skipped
    /// one, which never matches again).
    ///
    /// The statements are plain SQL that is valid on SQLite and PostgreSQL
    /// alike. <c>Down</c> deliberately does nothing: the state it would restore
    /// is the defect, and a reverse pass could not tell a converted row from a
    /// book that was always an ebook.
    /// </summary>
    public partial class ConvertLegacyPhysicalBooksWithFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Not a paper reading position. The paper reader saves the JSON
            // object {"page":N}; the PDF reader saves {"pageNumber":N,...}, which
            // does not match this pattern, and the EPUB reader an epubcfi string.
            const string DigitalProgress =
                """
                REPLACE(TRIM(COALESCE("Progress_LastLocation", '')), ' ', '') NOT LIKE '{"page":%'
                """;

            const string LegacyShape =
                """
                "BookType" = 'physical' AND "FileDetails_HasFile" = TRUE
                """;

            // Ebooks: the two physical columns have an ebook counterpart, so
            // they move rather than disappear.
            migrationBuilder.Sql(
                $$"""
                UPDATE "Books" SET
                    "BookType" = 'ebook',
                    "Isbn" = COALESCE("Isbn", "PhysicalBookModel_Isbn"),
                    "PageCount" = COALESCE("PageCount", "PhysicalBookModel_PageCount"),
                    "PhysicalBookModel_Isbn" = NULL,
                    "PhysicalBookModel_PageCount" = NULL
                WHERE {{LegacyShape}}
                  AND {{DigitalProgress}}
                  AND (
                      LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.epub'
                      OR LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.pdf'
                      OR LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.txt'
                      OR LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.mobi'
                      OR LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.azw3'
                  );
                """);

            // Audiobooks: nothing in the audio model holds an ISBN or a page
            // count, so those print-edition values go and the identifier
            // identity goes with them.
            migrationBuilder.Sql(
                $$"""
                UPDATE "Books" SET
                    "BookType" = 'audiobook',
                    "PhysicalBookModel_Isbn" = NULL,
                    "PhysicalBookModel_PageCount" = NULL,
                    "NormalizedIsbn" = NULL
                WHERE {{LegacyShape}}
                  AND {{DigitalProgress}}
                  AND (
                      LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.m4b'
                      OR LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.m4a'
                      OR LOWER(COALESCE("FileDetails_FileName", '')) LIKE '%.mp3'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A row that was converted cannot be told apart from a book that was
            // always an ebook or an audiobook, and relabelling every digital book
            // with a file as physical would recreate the defect for all of them.
            // The forward fix is not reversible by data alone.
        }
    }
}
