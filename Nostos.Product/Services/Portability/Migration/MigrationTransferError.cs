namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>Stable engine failures; the HTTP adapter maps these codes without parsing messages.</summary>
public sealed class MigrationTransferException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    internal static MigrationTransferException Error(string code) => new(code, code);
    public const string InvalidRequest = "migration_session_invalid";
    public const string InvalidState = "migration_invalid_state";
    public const string ReservationRequired = "migration_reservation_required";
    public const string Expired = "migration_session_expired";
    public const string IdentityMismatch = "migration_file_identity_mismatch";
    public const string RangeInvalid = "migration_chunk_range_invalid";
    public const string HashMismatch = "migration_chunk_hash_mismatch";
    public const string ChunkConflict = "migration_chunk_conflict";
    public const string MissingChunks = "migration_chunks_missing";
    public const string StorageExhausted = "migration_storage_exhausted";
    public const string MetadataRequired = "migration_chunk_metadata_required";
    public const string ImportPreparationUnavailable = "migration_import_preparation_unavailable";
    public const string ExportArtifactUnavailable = "migration_export_artifact_unavailable";
}

/// <summary>
/// Parsed transport metadata. Inclusive range endpoints, total and SHA-256 are
/// mandatory, and are checked against the session before any request bytes are written.
/// Slice 8 parses HTTP headers into this value.
/// </summary>
public sealed record MigrationChunkMetadata(long Start, long End, long Total, string Sha256);
