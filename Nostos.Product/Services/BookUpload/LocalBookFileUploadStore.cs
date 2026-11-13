using System.Security.Cryptography;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Backend.Services.Uploads;

namespace Nostos.Backend.Services.BookUpload;

/// <summary>
/// SelfHosted staging for resumable book files (#1266). One pre-sized file per
/// session receives parts at their offsets; each part is first received and
/// hashed into a unique temp file, then placed under the session's write lock,
/// so a retry can never interleave with a receipted range. The bytes go
/// through the shared <see cref="ResumablePartReceiver"/>, which uses one
/// 128 KiB budgeted copy buffer and never buffers a part in memory.
///
/// Recovery accepts either the staged or the sealed name, and always
/// re-verifies the whole-file SHA-256 before publication.
/// </summary>
public sealed class LocalBookFileUploadStore(TransferPathResolver paths) : IBookFileUploadStore
{
    public async Task PrepareAsync(Guid sessionId, long totalBytes, CancellationToken ct)
    {
        var part = paths.GetBookUploadPartPath(sessionId);
        if (File.Exists(paths.VerifyPathWithinRoot(part))) return;

        await using var stream = paths.CreateNewVerifiedFile(part);
        stream.SetLength(totalBytes); // Sparse/random-access allocation; receipts prove what arrived.
        await DurableFlushAsync(stream, ct);
    }

    public async Task<string> ReceivePartAsync(
        Guid sessionId,
        int partIndex,
        int lengthBytes,
        string expectedSha256,
        Stream content,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var pending = paths.GetBookUploadPendingPartPath(sessionId, partIndex);
        var outcome = await ResumablePartReceiver.ReceiveIntoAsync(
            () => paths.CreateNewVerifiedFile(pending),
            () => TryDelete(pending),
            content,
            lengthBytes,
            expectedSha256,
            ct);
        return outcome switch
        {
            ResumablePartOutcome.Received => pending,
            ResumablePartOutcome.RangeInvalid =>
                throw BookUploadException.Error(BookUploadException.PartRangeInvalid),
            _ => throw BookUploadException.Error(BookUploadException.PartHashMismatch),
        };
    }

    public async Task PlacePartAsync(
        Guid sessionId,
        string pendingToken,
        long offsetBytes,
        CancellationToken ct)
    {
        var pending = paths.VerifyPathWithinRoot(pendingToken);
        var part = paths.VerifyPathWithinRoot(paths.GetBookUploadPartPath(sessionId));
        await using (var source = new FileStream(
            pending, FileMode.Open, FileAccess.Read, FileShare.Read,
            1, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await using var target = new FileStream(
                part, FileMode.Open, FileAccess.Write, FileShare.Read,
                1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            target.Position = offsetBytes;
            using var buffer = new PortableArchiveBufferBudget(ResumablePartReceiver.BufferBytes)
                .Rent(ResumablePartReceiver.BufferBytes, ct);
            int read;
            while ((read = await source.ReadAsync(buffer.Memory, ct)) != 0)
                await target.WriteAsync(buffer.Memory[..read], ct);
            await DurableFlushAsync(target, ct);
        }

        TryDelete(pending);
    }

    public async Task<bool> VerifyAsync(
        Guid sessionId,
        long totalBytes,
        string expectedSha256,
        CancellationToken ct)
    {
        var ready = paths.VerifyPathWithinRoot(paths.GetBookUploadReadyPath(sessionId));
        var part = paths.VerifyPathWithinRoot(paths.GetBookUploadPartPath(sessionId));
        var path = File.Exists(ready) ? ready : part;
        if (!File.Exists(path)) return false;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != totalBytes) return false;

        using var buffer = new PortableArchiveBufferBudget(ResumablePartReceiver.BufferBytes)
            .Rent(ResumablePartReceiver.BufferBytes, ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = await stream.ReadAsync(buffer.Memory, ct)) != 0)
            hash.AppendData(buffer.Memory.Span[..read]);
        return string.Equals(
            Convert.ToHexStringLower(hash.GetHashAndReset()),
            expectedSha256,
            StringComparison.Ordinal);
    }

    public Task SealAsync(Guid sessionId, CancellationToken ct)
    {
        var ready = paths.VerifyPathWithinRoot(paths.GetBookUploadReadyPath(sessionId));
        if (File.Exists(ready)) return Task.CompletedTask;

        var part = paths.VerifyPathWithinRoot(paths.GetBookUploadPartPath(sessionId));
        File.Move(part, ready, overwrite: false);
        return Task.CompletedTask;
    }

    public Task<bool> HasStagingAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult(
            File.Exists(paths.VerifyPathWithinRoot(paths.GetBookUploadReadyPath(sessionId)))
            || File.Exists(paths.VerifyPathWithinRoot(paths.GetBookUploadPartPath(sessionId))));

    public Task<Stream> OpenSealedAsync(Guid sessionId, CancellationToken ct)
    {
        var ready = paths.VerifyPathWithinRoot(paths.GetBookUploadReadyPath(sessionId));
        var part = paths.VerifyPathWithinRoot(paths.GetBookUploadPartPath(sessionId));
        var path = File.Exists(ready) ? ready : part;
        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public async Task AdoptAsync(
        Guid bookId,
        Guid sessionId,
        string fileName,
        IBookAssetStorage storage,
        CancellationToken ct)
    {
        var ready = paths.VerifyPathWithinRoot(paths.GetBookUploadReadyPath(sessionId));
        await storage.AdoptBookFileAsync(bookId, ready, fileName, ct);
        DeleteSessionDirectory(sessionId);
    }

    public Task DiscardAsync(Guid sessionId, CancellationToken ct)
    {
        DeleteSessionDirectory(sessionId);
        return Task.CompletedTask;
    }

    public void DiscardPending(string pendingToken) => TryDelete(pendingToken);

    private void DeleteSessionDirectory(Guid sessionId)
    {
        var directory = paths.VerifyPathWithinRoot(paths.GetBookUploadSessionDirectory(sessionId));
        if (!Directory.Exists(directory)) return;

        // Never recursive: only files this store generated can exist here.
        foreach (var file in Directory.EnumerateFiles(directory))
            TryDelete(file);
        try { Directory.Delete(directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void TryDelete(string path)
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
