namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// The finished local file of a single-file acquisition, in the format the
/// library will store it as. It is what the pipeline hands to storage, and the
/// only thing that outlives the staging directory.
/// </summary>
public sealed record AcquisitionArtifact(
    string FilePath,
    string FileExtension,
    string ContentType);

/// <summary>
/// A part as it actually landed on disk. <see cref="FilePath"/> is a path the
/// acquisition layer generated inside its own staging directory — never anything
/// derived from a remote filename.
/// </summary>
public sealed record AcquisitionPart(string FilePath, string FileExtension, long Bytes);

/// <summary>
/// One downloaded part of a multi-track audiobook, measured from the bytes that
/// were actually received. The source's own claims about length and size are
/// deliberately not used: per-section playtimes are rounded and drift, and the
/// feed publishes no sizes at all.
/// </summary>
public sealed record MeasuredTrack(
    AcquisitionPart Part,
    int Number,
    string? Title,
    long DurationMs,
    uint Crc32);
