using Microsoft.Extensions.Options;
using Nostos.Backend.Services.Library.Imports;

namespace Nostos.Backend.Services.Calibre;

/// <summary>
/// The one Calibre library folder this server may read. The administrator makes
/// the folder visible (for a container, a read-only bind mount such as
/// <c>/mnt/calibre:/external/calibre:ro</c>) and names it here; the web
/// interface never sends a path.
///
/// Only <see cref="ReadAsync"/>, <see cref="OpenRead"/> and
/// <see cref="OpenCover"/> exist, and every open is read-only. The database is
/// opened with SQLite's readonly mode as well as <c>PRAGMA query_only</c>, and
/// the files are opened with read sharing, so the library can stay in use by
/// Calibre while an import reads it.
///
/// The reading itself lives in <see cref="CalibreLibraryReader"/>, shared with
/// the uploaded-library source (#1572), so both sources map Calibre the same
/// way.
/// </summary>
public sealed class CalibreFolderSource : ICalibreLibrarySource, ICalibreLibraryAvailability
{
    public const string SourceKind = "calibre";

    /// <summary>Upper bound on books read in one scan, so a mistaken mount cannot run unbounded.</summary>
    public const int MaxBooks = CalibreLibraryReader.MaxBooks;

    private readonly CalibreLibraryReader? _reader;

    public CalibreFolderSource(IOptions<CalibreOptions> options)
    {
        var value = options.Value;
        var configured = value.LibraryRoot?.Trim();
        if (string.IsNullOrEmpty(configured))
            return;

        if (!Path.IsPathFullyQualified(configured))
        {
            throw new InvalidOperationException(
                $"'{CalibreOptions.SectionName}:LibraryRoot' must be an absolute path to the Calibre library folder.");
        }

        _reader = new CalibreLibraryReader(configured);
    }

    public string Kind => SourceKind;

    public bool IsConfigured => _reader is not null;

    public bool IsAvailable => _reader?.IsAvailable == true;

    public string? CoverExtension => ".jpg";

    public Task<CalibreSourceLibrary> ReadAsync(CancellationToken ct) =>
        _reader is null
            ? throw new CalibreLibraryException(
                "The Calibre library is not available. Check that the folder is mounted and holds metadata.db.")
            : _reader.ReadAsync(ct);

    public Stream? OpenRead(CalibreSourceFormat format) => _reader?.OpenRead(format);

    public Stream? OpenCover(CalibreSourceBook book) => _reader?.OpenCover(book);
}
