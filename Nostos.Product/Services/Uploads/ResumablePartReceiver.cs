using System.Security.Cryptography;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Services.Uploads;

/// <summary>How one received resumable part ended.</summary>
public enum ResumablePartOutcome
{
    /// <summary>The body held its declared length and its SHA-256 matched.</summary>
    Received,

    /// <summary>The body was shorter or longer than its declared range.</summary>
    RangeInvalid,

    /// <summary>The body held its declared length but not its SHA-256.</summary>
    HashMismatch,
}

/// <summary>
/// The one place resumable part bytes are copied and hashed (#1629). The book
/// file upload and the Calibre library upload receive a part through the same
/// loop: one 128 KiB budgeted buffer so a part is never held in memory, a read
/// bounded at one byte past the declared length so an oversized body is
/// refused instead of drained, and an <see cref="IncrementalHash"/> over
/// exactly the bytes written.
///
/// The caller keeps its own error vocabulary: this reports the outcome and the
/// store maps it to the code its wire contract already carries, so neither
/// upload's response body changes.
/// </summary>
public static class ResumablePartReceiver
{
    /// <summary>The copy buffer, and the ceiling for buffering one part.</summary>
    public const int BufferBytes = 128 * 1024;

    /// <summary>
    /// Receives one part from <paramref name="content"/> into
    /// <paramref name="output"/>, hashing the same bytes. The output is
    /// flushed once the part is accepted; on any other outcome it is left as
    /// received for the caller to discard. <paramref name="expectedSha256"/>
    /// is the normalized lowercase hex digest the caller validated.
    /// </summary>
    public static async Task<ResumablePartOutcome> ReceiveAsync(
        Stream content,
        Stream output,
        long expectedLength,
        string expectedSha256,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(expectedSha256);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);

        using var buffer = new PortableArchiveBufferBudget(BufferBytes).Rent(BufferBytes, ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        while (true)
        {
            // Read at most length+1: an oversized body is rejected without
            // draining it into storage.
            var count = (int)Math.Min(buffer.Length, expectedLength - received + 1);
            var read = await content.ReadAsync(buffer.Memory[..count], ct);
            if (read == 0) break;
            received += read;
            if (received > expectedLength) return ResumablePartOutcome.RangeInvalid;
            hash.AppendData(buffer.Memory.Span[..read]);
            await output.WriteAsync(buffer.Memory[..read], ct);
        }

        if (received != expectedLength) return ResumablePartOutcome.RangeInvalid;
        if (!string.Equals(
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                expectedSha256,
                StringComparison.Ordinal))
        {
            return ResumablePartOutcome.HashMismatch;
        }

        await output.FlushAsync(ct);
        return ResumablePartOutcome.Received;
    }

    /// <summary>
    /// Receives one part into a temporary file and removes that file when the
    /// part is refused or the copy throws, so a rejected part never leaves a
    /// partial file behind for the next attempt to trip over.
    /// </summary>
    public static async Task<ResumablePartOutcome> ReceiveIntoAsync(
        Func<Stream> createOutput,
        Action discardOutput,
        Stream content,
        long expectedLength,
        string expectedSha256,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(createOutput);
        ArgumentNullException.ThrowIfNull(discardOutput);

        try
        {
            ResumablePartOutcome outcome;
            await using (var output = createOutput())
            {
                outcome = await ReceiveAsync(content, output, expectedLength, expectedSha256, ct);
            }

            if (outcome != ResumablePartOutcome.Received) discardOutput();
            return outcome;
        }
        catch
        {
            discardOutput();
            throw;
        }
    }
}
