using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// Durable receipt for one accepted book-upload part (#1266). Inserted only
// after the bytes are durably placed at their offset. Part of the composite
// (SessionId, PartIndex) primary key; no byte payload is stored in the
// database.
public class BookUploadPartReceiptRecord
{
    public Guid SessionId { get; set; }

    public int PartIndex { get; set; }

    public long OffsetBytes { get; set; }

    public int LengthBytes { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
}
