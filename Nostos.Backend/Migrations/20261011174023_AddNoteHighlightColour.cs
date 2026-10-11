using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteHighlightColour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HighlightColour",
                table: "Notes",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            // A PDF highlight has always kept its pen inside the JSON location
            // payload in CfiRange ({"pageNumber":..,"rects":..,"colour":"sage"}).
            // Copy it into the column so the column is the one place a pen is
            // read from and existing PDF colours are not lost. The payload is
            // left as it was. json_extract raises on malformed JSON, and an
            // EPUB CfiRange ("epubcfi(...)") is not JSON at all, so the
            // extraction is guarded by json_valid inside a CASE rather than
            // relying on AND evaluation order. Everything else stays NULL:
            // "no pen recorded", which readers draw in the default amber.
            migrationBuilder.Sql(
                """
                UPDATE Notes
                SET HighlightColour =
                    CASE WHEN json_valid(CfiRange)
                         THEN json_extract(CfiRange, '$.colour')
                    END
                WHERE CfiRange IS NOT NULL
                  AND CASE WHEN json_valid(CfiRange)
                           THEN json_extract(CfiRange, '$.colour')
                      END IN ('amber', 'sage', 'blue', 'rose');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Write each PDF highlight's pen back into its location payload
            // before the column goes, so a recolour made while the column
            // existed survives the rollback. An EPUB location is a bare CFI
            // with nowhere to carry a colour: those return to being drawn in
            // the book's current pen, as they were before this migration.
            migrationBuilder.Sql(
                """
                UPDATE Notes
                SET CfiRange = json_set(CfiRange, '$.colour', HighlightColour)
                WHERE HighlightColour IS NOT NULL
                  AND CfiRange IS NOT NULL
                  AND CASE WHEN json_valid(CfiRange)
                           THEN json_type(CfiRange)
                      END = 'object';
                """);

            migrationBuilder.DropColumn(
                name: "HighlightColour",
                table: "Notes");
        }
    }
}
