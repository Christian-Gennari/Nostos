using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// Exact-once command record for library mutations. The unique
// (ClientId, IdempotencyKey) index makes retried UI/MCP/connector commands
// return the stored response without re-running the command. It is deliberately
// separate from NoteCommandReceipt so a note mutation can never replay a
// library response (and vice versa). Inputs are bounded: the
// MaxLength annotations document the limits, and the
// CK_LibraryCommandReceipts_Bounds CHECK constraint enforces them in SQLite.
public class LibraryCommandReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)]
    public string ClientId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(32)]
    public string CommandKind { get; set; } = string.Empty;

    [MaxLength(131072)]
    public string ResponseJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
