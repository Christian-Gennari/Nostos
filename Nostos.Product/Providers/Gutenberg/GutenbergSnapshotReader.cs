using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Reads Project Gutenberg's daily bulk archive: a zip containing one tar, with
/// one RDF record per ebook. Every member that is not an ebook RDF file is
/// skipped, and both the tar header's declared size and the parsed record are
/// bounded so a zip bomb cannot exhaust memory.
///
/// The reader takes a complete file on disk and yields parsed
/// <see cref="GutenbergBook"/> records in archive order. It has no HTTP and no
/// provider-item mapping, which is what makes it testable against an archive
/// built in memory.
/// </summary>
internal sealed class GutenbergSnapshotReader
{
    private readonly GutenbergSnapshotLimits _snapshotLimits;
    private readonly string _providerId;

    public GutenbergSnapshotReader(GutenbergSnapshotLimits snapshotLimits, string providerId)
    {
        _snapshotLimits = snapshotLimits;
        _providerId = providerId;
    }

    /// <summary>
    /// One ebook per call, stopping at the end of the archive. Parse failures
    /// are translated here because an iterator cannot catch and <c>yield</c> in
    /// the same block.
    /// </summary>
    public async IAsyncEnumerable<GutenbergBook> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var zip = OpenArchive(file);

        var tarEntry = zip.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            ?? throw ProviderException.InvalidResponse(_providerId, "the snapshot archive has no tar catalog");

        await using var tarStream = OpenTar(tarEntry);
        using var tar = new TarReader(tarStream);

        while (await ReadItemAsync(tar, ct) is { } book)
            yield return book;
    }

    private async Task<GutenbergBook?> ReadItemAsync(TarReader tar, CancellationToken ct)
    {
        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct);
            }
            catch (Exception ex) when (IsArchiveFailure(ex))
            {
                throw ProviderException.InvalidResponse(_providerId, ex.Message);
            }

            if (entry is null)
                return null;

            ct.ThrowIfCancellationRequested();

            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                continue;

            if (GutenbergCatalog.SnapshotIdFromEntryName(entry.Name) is not { } id)
                continue;

            // The tar header declares the member's length; reject an
            // implausible one before XDocument can materialise it, so a zip
            // bomb cannot exhaust memory through a single record.
            if (entry.Length > _snapshotLimits.MaxMemberBytes)
            {
                throw ProviderException.InvalidResponse(
                    _providerId,
                    $"snapshot entry '{entry.Name}' is over the {_snapshotLimits.MaxMemberBytes} byte member limit");
            }

            GutenbergBook? book;
            try
            {
                var document = await XDocument.LoadAsync(entry.DataStream, LoadOptions.None, ct);
                book = GutenbergCatalog.ParseSnapshot(id, document);
            }
            catch (XmlException ex)
            {
                throw ProviderException.InvalidResponse(_providerId, $"snapshot entry '{entry.Name}': {ex.Message}");
            }
            catch (Exception ex) when (IsArchiveFailure(ex))
            {
                // The member's declared length can outrun the bytes that are
                // actually there; that is a corrupt response, not a host error.
                throw ProviderException.InvalidResponse(_providerId, $"snapshot entry '{entry.Name}': {ex.Message}");
            }

            if (book is not null)
                return book;
        }
    }

    private ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            throw ProviderException.InvalidResponse(_providerId, ex.Message);
        }
    }

    private Stream OpenTar(ZipArchiveEntry entry)
    {
        try
        {
            return entry.Open();
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            throw ProviderException.InvalidResponse(_providerId, ex.Message);
        }
    }

    /// <summary>
    /// A truncated or corrupt archive: <see cref="EndOfStreamException"/> and
    /// other <see cref="IOException"/>s when the stream ends mid-structure,
    /// <see cref="InvalidDataException"/> for bad headers and unsupported
    /// compression. None of these is a host failure, so all of them become
    /// <c>provider_response_invalid</c>. Cancellation is not an IOException and
    /// deliberately keeps propagating.
    /// </summary>
    private static bool IsArchiveFailure(Exception ex) => ex is IOException or InvalidDataException;
}
