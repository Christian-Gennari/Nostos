using System.Security.Cryptography;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// One ranged archive file per session, with unique chunk-sized temporary
/// files received without database write locks. Uses one 128 KiB budgeted copy buffer;
/// request streams are read asynchronously. FlushAsync followed by Flush(true)
/// is required before a receipt commits. This protects process restart; directory
/// fsync/power-loss durability of rename is filesystem dependent. Recovery accepts
/// either generated archive name and re-verifies identity before publishing.
/// The resolver's operator-owned root (including a configured root symlink) must
/// be protected from hostile local writers; post-open checks are defense in depth.
/// </summary>
public class FileMigrationUploadStore(TransferPathResolver paths)
{
    internal const int BufferBytes = 128 * 1024;
    internal TransferPathResolver Paths => paths;
    private readonly MigrationFileMutex _mutex = new(paths);
    internal Task<IAsyncDisposable> EnterJobAsync(Guid jobId, CancellationToken ct) => _mutex.EnterAsync(jobId, ct);

    /// <summary>Deterministic storage checkpoints; production performs no extra work.</summary>
    internal Task BeforeDetachedDeleteAsync(CancellationToken ct) => BeforeScopeDeleteAsync(ct);
    protected virtual Task BeforeScopeDeleteAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual Task BeforeHashAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual Task AfterChunkFlushAsync(CancellationToken ct) => Task.CompletedTask;

    internal FileStream Open(string path, FileAccess access)
    {
        path = paths.VerifyPathWithinRoot(path);
        var stream = new FileStream(path, FileMode.Open, access, FileShare.Read,
            1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try { paths.VerifyPathWithinRoot(path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    internal async Task CreateAsync(Guid sessionId, long size, CancellationToken ct)
    {
        await using var stream = paths.CreateNewVerifiedFile(paths.GetUploadArchivePartPath(sessionId));
        stream.SetLength(size); // Sparse/random-access allocation; receipts, never length, prove received data.
        await DurableFlushAsync(stream, ct);
    }

    /// <summary>Storage-failure injection seam. Production always uses the verified resolver.</summary>
    protected virtual FileStream CreateChunkFile(string path) => paths.CreateNewVerifiedFile(path);

    /// <summary>Injects write failures without exhausting the host disk in tests.</summary>
    protected virtual ValueTask WriteChunkBytesAsync(FileStream output, ReadOnlyMemory<byte> bytes, CancellationToken ct) => output.WriteAsync(bytes, ct);

    internal async Task<string> ReceiveAsync(Guid sessionId, int index, int expectedBytes,
        string expectedHash, Stream content, PortableArchiveBufferBudget budget, CancellationToken ct)
    {
        var temp = paths.GetUploadChunkTempPath(sessionId, index);
        try
        {
            await using var output = CreateChunkFile(temp);
            using var buffer = budget.Rent(BufferBytes, ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            while (true)
            {
                // Read at most expected+1; reject oversized bodies without draining them.
                var count = (int)Math.Min(buffer.Memory.Length, expectedBytes - received + 1);
                var read = await content.ReadAsync(buffer.Memory[..count], ct);
                if (read == 0) break;
                received += read;
                if (received > expectedBytes)
                    throw MigrationTransferException.Error(MigrationTransferException.RangeInvalid);
                hash.AppendData(buffer.Memory.Span[..read]);
                await WriteChunkBytesAsync(output, buffer.Memory[..read], ct);
            }
            if (received != expectedBytes)
                throw MigrationTransferException.Error(MigrationTransferException.RangeInvalid);
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash)
                throw MigrationTransferException.Error(MigrationTransferException.HashMismatch);
            await output.FlushAsync(ct);
            return temp;
        }
        catch { TryDelete(temp); throw; }
    }

    internal async Task PlaceAsync(Guid sessionId, string temp, long offset,
        PortableArchiveBufferBudget budget, CancellationToken ct)
    {
        await using var source = Open(temp, FileAccess.Read);
        await using var target = Open(paths.GetUploadArchivePartPath(sessionId), FileAccess.Write);
        target.Position = offset;
        using var buffer = budget.Rent(BufferBytes, ct);
        int read;
        while ((read = await source.ReadAsync(buffer.Memory, ct)) != 0)
            await target.WriteAsync(buffer.Memory[..read], ct);
        await DurableFlushAsync(target, ct);
        await AfterChunkFlushAsync(ct);
    }

    internal async Task<bool> VerifyAsync(Guid sessionId, long size, string checksum,
        PortableArchiveBufferBudget budget, CancellationToken ct)
    {
        await BeforeHashAsync(ct);
        var part = paths.VerifyPathWithinRoot(paths.GetUploadArchivePartPath(sessionId));
        var final = paths.VerifyPathWithinRoot(paths.GetUploadArchivePath(sessionId));
        // A crash may occur after rename and before the DB commit.
        await using var stream = Open(File.Exists(part) ? part : final, FileAccess.Read);
        if (stream.Length != size) return false;
        using var buffer = budget.Rent(BufferBytes, ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = await stream.ReadAsync(buffer.Memory, ct)) != 0)
            hash.AppendData(buffer.Memory.Span[..read]);
        return Convert.ToHexStringLower(hash.GetHashAndReset()) == checksum;
    }

    internal void Seal(Guid sessionId)
    {
        var part = paths.VerifyPathWithinRoot(paths.GetUploadArchivePartPath(sessionId));
        var final = paths.VerifyPathWithinRoot(paths.GetUploadArchivePath(sessionId));
        if (File.Exists(part)) File.Move(part, final, overwrite: false);
    }

    internal bool HasArchive(Guid sessionId) =>
        File.Exists(paths.VerifyPathWithinRoot(paths.GetUploadArchivePartPath(sessionId))) ||
        File.Exists(paths.VerifyPathWithinRoot(paths.GetUploadArchivePath(sessionId)));

    internal void DiscardUnpublishedSession(Guid sessionId)
    {
        File.Delete(paths.VerifyPathWithinRoot(paths.GetUploadArchivePartPath(sessionId)));
        var directory = paths.VerifyPathWithinRoot(paths.GetUploadSessionDirectory(sessionId));
        if (Directory.Exists(directory)) Directory.Delete(directory); // Empty scope only; never recursive.
    }

    internal void TryDelete(string path)
    {
        try { File.Delete(paths.VerifyPathWithinRoot(path)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (TransferPathException) { }
    }

    private static async Task DurableFlushAsync(FileStream stream, CancellationToken ct)
    {
        await stream.FlushAsync(ct);
        ct.ThrowIfCancellationRequested();
        stream.Flush(flushToDisk: true);
    }
}
