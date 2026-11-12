using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

/// <summary>
/// The library's connection to Readwise's export API (issue #1525). One row
/// per library: the access token, encrypted with ASP.NET Data Protection
/// ciphertext exactly like the AI provider keys, and the watermark that makes
/// a second import fetch only what changed.
///
/// <para>
/// The token is host-local operational state. It is never part of a portable
/// archive, never returned to a client, and never written to a log. The
/// SelfHosted backup archive is scrubbed of this row, so a restored backup
/// asks for the token again rather than carrying it to another machine.
/// </para>
/// </summary>
public class ReadwiseConnectionModel
{
    /// <summary>The fixed primary key of the one connection row.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Data-Protection ciphertext of the Readwise access token.</summary>
    [MaxLength(4096)]
    public string TokenEncrypted { get; set; } = string.Empty;

    /// <summary>
    /// The start of the last successful import, as Readwise's
    /// <c>updatedAfter</c> watermark. Null means "fetch the whole account".
    /// </summary>
    public DateTime? UpdatedAfterUtc { get; set; }

    /// <summary>When an import last committed, or null when none has.</summary>
    public DateTime? LastImportedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
