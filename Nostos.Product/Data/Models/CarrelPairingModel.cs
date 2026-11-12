using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

/// <summary>The states a pairing row moves through. Only <see cref="Pending"/> is waiting for a person.</summary>
public static class CarrelPairingStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
}

/// <summary>
/// One pairing attempt (Carrel Sync Protocol v1, "Pairing"): a device asks for
/// a code, the owner approves it on <c>/pair</c>, and the device collects its
/// token by polling. The row is short-lived operational state.
///
/// The token handed to an approved device is kept as Data-Protection
/// ciphertext only until the polling window closes, so the plaintext device
/// credential is not at rest in the clear; the long-lived device row keeps
/// only its hash.
/// </summary>
public class CarrelPairingModel
{
    public const int MaxDeviceNameLength = 100;

    public const int MaxPublicKeyLength = 256;

    /// <summary>Lowercase hex SHA-256, 64 characters.</summary>
    public const int HashLength = 64;

    /// <summary>The pairing id the device polls with (UUIDv7, server-made).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The human code in <c>XXX-XXX</c> form; unique among live pairings.</summary>
    [Required]
    [MaxLength(7)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxDeviceNameLength)]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(MaxPublicKeyLength)]
    public string DevicePublicKey { get; set; } = string.Empty;

    /// <summary>One of <see cref="CarrelPairingStatus"/>.</summary>
    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = CarrelPairingStatus.Pending;

    /// <summary>The account that approved it. Null while nobody has.</summary>
    [MaxLength(200)]
    public string? OwnerKey { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>After this the code can no longer be approved.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }

    /// <summary>The device row approval made. Null until approved.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Data-Protection ciphertext of the token the polling device collects,
    /// cleared once the handoff window ends. Never logged, never listed.
    /// </summary>
    [MaxLength(4096)]
    public string? TokenEncrypted { get; set; }
}
