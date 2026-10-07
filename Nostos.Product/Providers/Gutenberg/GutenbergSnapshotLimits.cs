namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Hard byte caps for one bulk snapshot read, so a corrupt or hostile archive
/// cannot exhaust the host's disk (the download) or memory (one parsed member).
///
/// The defaults are generous multiples of the live catalogue — about 177 MB
/// zipped, with individual RDF records in the tens of kilobytes — so ordinary
/// growth does not trip them. Tests inject small values to exercise the caps.
/// </summary>
public sealed record GutenbergSnapshotLimits
{
    /// <summary>
    /// Cap on the zipped archive written to the temp file. Checked against the
    /// declared length and, because that is only the source's claim, while the
    /// body is streamed.
    /// </summary>
    public long MaxSnapshotBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// Cap on one uncompressed tar member before it is parsed. The tar header's
    /// declared length is rejected first, so a zip bomb cannot inflate a single
    /// RDF record into unbounded memory.
    /// </summary>
    public long MaxMemberBytes { get; init; } = 16L * 1024 * 1024;
}
