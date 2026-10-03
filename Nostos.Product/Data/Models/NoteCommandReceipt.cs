using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// Exact-once command record for assistant note mutations (issue #260 §2, §4).
// The unique (ClientId, IdempotencyKey) index makes retried capture commands
// return the stored response without creating a duplicate note. Deliberately a
// separate table from LibraryCommandReceipt so a note command can never replay
// a library response (and vice versa). Inputs are bounded: the MaxLength
// annotations document the limits, and the CK_NoteCommandReceipts_Bounds CHECK
// constraint enforces them in SQLite.
public class NoteCommandReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)]
    public string ClientId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Command { get; set; } = string.Empty;

    [MaxLength(131072)]
    public string ResultJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
