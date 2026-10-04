using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// Durable receipt for one accepted upload chunk (issue #679). The receipt is
// inserted only after the chunk bytes are durably flushed to the session file;
// no byte payload is ever stored in the database. Part of the composite
// (SessionId, ChunkIndex) primary key. Host-local operational transfer state,
// never portable library content.
public class MigrationChunkReceiptRecord
{
    public Guid SessionId { get; set; }

    public int ChunkIndex { get; set; }

    public long OffsetBytes { get; set; }

    public int LengthBytes { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
