using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

/// <summary>
/// One Carrel writing device paired with this Nostos account (Carrel Sync
/// Protocol v1, <c>/carrel/v1</c>).
///
/// Only the SHA-256 hash of the device token is kept: the token itself is
/// handed to the device once through the pairing poll and can never be read
/// back. Revoking deletes the row, so the next request with that token is
/// refused. A device belongs to one owner (<see cref="OwnerKey"/>), which is
/// how a host with accounts keeps one person's devices out of another's inbox.
/// Host-local credentials, never part of a portable archive.
/// </summary>
public class CarrelDeviceModel
{
    public const int MaxNameLength = 100;

    public const int MaxPublicKeyLength = 256;

    /// <summary>
    /// Lowercase hex SHA-256 of the whole token, <c>crl_</c> prefix included
    /// (or <c>stl_</c> for a device paired before the rename).
    /// </summary>
    public const int HashLength = 64;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The account this device syncs for. One key per library.</summary>
    [Required]
    [MaxLength(200)]
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>The device's own label (<c>X250</c>), shown in Settings → Devices.</summary>
    [Required]
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The device's Ed25519 public key, hex, as pairing reported it (protocol v1 records it).</summary>
    [MaxLength(MaxPublicKeyLength)]
    public string PublicKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(HashLength)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the device last synced. Written at most once a minute.</summary>
    public DateTime? LastUsedAtUtc { get; set; }
}
