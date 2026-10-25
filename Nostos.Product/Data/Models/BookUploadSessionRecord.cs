using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nostos.Backend.Services.BookUpload;

namespace Nostos.Backend.Data.Models;

// Durable operational record for one resumable book-file upload (#1266).
// Host-local transfer bookkeeping: part receipts, staging location and the
// client fingerprint are never portable library content. The book row is not
// hidden or moved into a special status while this session is active; the
// session alone says an upload is in progress.
public class BookUploadSessionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }

    /// <summary>
    /// Owner scope established by the host (a constant on SelfHosted, the
    /// account claim when a host supplies accounts). Every read and mutation is
    /// scoped to it; a mismatch is reported as not-found.
    /// </summary>
    [MaxLength(128)]
    public string OwnerKey { get; set; } = string.Empty;

    public int State { get; set; }

    public long TotalBytes { get; set; }

    public int ChunkSize { get; set; }

    public int TotalChunks { get; set; }

    /// <summary>The client's original file name; only its validated extension is published.</summary>
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? ClientContentType { get; set; }

    [MaxLength(64)]
    public string FileSha256 { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? ClientFingerprint { get; set; }

    public long ReceivedBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Sliding expiry, extended by every accepted part.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Non-null only while the session is active, so a unique index enforces
    /// at most one active upload per (owner, book). Cleared on every terminal
    /// transition; SQLite permits many NULLs in a unique index.
    /// </summary>
    [MaxLength(200)]
    public string? ActiveSessionKey { get; set; }

    [MaxLength(64)]
    public string? FailureCode { get; set; }

    public long Version { get; set; }

    [NotMapped]
    public BookUploadState StateValue
    {
        get => (BookUploadState)State;
        set => State = (int)value;
    }
}
