using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// An archive written before #332 can hold a book that is
/// <c>physical</c> and carries a book file: older builds defaulted Add Book to
/// "Physical book" and accepted an upload on any row. The archive is not
/// rejected for it — the shape was legitimate when it was written — it is
/// converted on restore by the same rule the database migration applies
/// (<see cref="LegacyPhysicalFileBook"/>), so a restored library and an upgraded
/// one agree.
///
/// Verification reads the archive through this too, because it compares the
/// restored rows against what the archive says; without it every converted book
/// would look like a mismatch.
/// </summary>
internal static class PortableLegacyPhysicalFileBooks
{
    /// <summary>
    /// The payload as the current model reads it. Call it once, on the freshly
    /// deserialised payload and its prepared media: the format is recorded
    /// nowhere else, since the relational payload carries no file extension.
    /// </summary>
    public static PortableLibraryData ToCurrent(
        PortableLibraryData data,
        IReadOnlyList<PortablePreparedMedia> stagedMedia)
    {
        if (!data.Books.Any(book => book.Type == "physical"))
            return data;

        var bookFiles = stagedMedia
            .Where(media => media.Descriptor.Kind == PortableArchiveFormat.BookMediaKind)
            .ToDictionary(media => media.Descriptor.BookId, media => media.Descriptor.FileName);

        return data with
        {
            Books = data.Books
                .Select(book => ToCurrent(book, bookFiles.GetValueOrDefault(book.Id)))
                .ToList(),
        };
    }

    /// <summary>
    /// One book, with the archive's own name for its file.
    /// </summary>
    public static PortableBook ToCurrent(PortableBook book, string? bookFileName)
    {
        if (book.Type != "physical")
            return book;

        return LegacyPhysicalFileBook.ResolveForRestore(bookFileName) switch
        {
            LegacyPhysicalFileBook.Resolution.Ebook => book with { Type = "ebook" },

            // An audiobook has no ISBN or page count of its own: they describe
            // the print edition, so they go rather than travel as unreadable
            // fields.
            LegacyPhysicalFileBook.Resolution.Audiobook => book with
            {
                Type = "audiobook",
                Isbn = null,
                PageCount = null,
            },

            _ => book,
        };
    }
}
