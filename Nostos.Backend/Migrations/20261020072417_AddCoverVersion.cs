using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddCoverVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileDetails_CoverVersion",
                table: "Books",
                type: "TEXT",
                nullable: true);

            // Every existing cover gets a token at once, so the library serves
            // cacheable cover URLs from the first read. Any value works: no
            // browser holds a ?v= URL yet. Rows without a cover stay null.
            migrationBuilder.Sql(
                "UPDATE \"Books\" SET \"FileDetails_CoverVersion\" = lower(hex(randomblob(8))) " +
                "WHERE \"FileDetails_CoverFileName\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Derived data only: the cover itself stays in CoverFileName and on
            // disk, and clients fall back to revalidated, unversioned URLs.
            migrationBuilder.DropColumn(
                name: "FileDetails_CoverVersion",
                table: "Books");
        }
    }
}
