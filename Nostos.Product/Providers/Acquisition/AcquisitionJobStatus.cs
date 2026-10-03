using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Nostos.Backend.Providers.Acquisition;

public enum AcquisitionJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// What the UI polls while an import runs. Every field the client needs is
/// here, so progress never requires the caller to hold the acquisition open.
/// </summary>
public sealed record AcquisitionJobStatus(
    string JobId,
    AcquisitionJobState State,
    string Stage,
    int Percent,
    string? Detail,
    string ProviderId,
    string ExternalId,
    string? AssetId,
    Guid? BookId,
    string? ErrorCode,
    string? Message,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public bool IsFinished => State is AcquisitionJobState.Succeeded
        or AcquisitionJobState.Failed
        or AcquisitionJobState.Cancelled;

    /// <summary>
    /// The progress a CLIENT may render, 0-100.
    ///
    /// Capped at 99 for every state except Succeeded, on purpose. A running
    /// acquisition can honestly report 100% of its own work — every byte is on
    /// disk — while the row is still being committed and the file moved into
    /// place. A UI that renders "100%" (or worse, treats it as Ready) at that
    /// moment hands the reader a Play button for a book whose file is not there
    /// yet, and the request 404s. 100 is a claim about the LIBRARY, so only a
    /// terminal Succeeded state may make it.
    /// </summary>
    public int ReportedPercent =>
        State != AcquisitionJobState.Succeeded && Percent >= 100
            ? 99
            : Math.Clamp(Percent, 0, 100);
}

/// <summary>
/// Runs acquisitions in the background so a long one can report progress.
///
/// A whole audiobook — dozens of tracks plus a multi-hour encode — takes far
/// longer than any sensible HTTP request, and a synchronous endpoint could only
/// ever answer by timing out with no information. Starting a job and polling its
/// status keeps the transport bounded while the work carries on.
/// </summary>
public interface IAcquisitionJobManager
{
    /// <summary>
    /// Queues an acquisition and returns immediately. Throws when the job store
    /// is saturated rather than silently queueing unbounded work.
    /// </summary>
    AcquisitionJobStatus Start(AcquisitionRequest request);

    AcquisitionJobStatus? Get(string jobId);

    /// <summary>
    /// Every job this process still holds, oldest first — including jobs that
    /// finished inside the retention window.
    ///
    /// Finished jobs are included deliberately: the TRANSITION to a terminal
    /// state is itself something a watcher has to be told exactly once, and a
    /// list that dropped a job the moment it finished would make that
    /// transition unobservable — the job would simply disappear from the feed
    /// with no outcome. Callers that only want work in flight filter on
    /// <see cref="AcquisitionJobStatus.IsFinished"/>.
    /// </summary>
    IReadOnlyList<AcquisitionJobStatus> List();

    /// <summary>Only the jobs still queued or running.</summary>
    IReadOnlyList<AcquisitionJobStatus> ListActive();

    /// <summary>Cancels a queued or running job. False when there is nothing to cancel.</summary>
    bool Cancel(string jobId);
}

/// <summary>
/// Stable product-level messages shared by the acquisition status feed and
/// host-specific reconciliation workers.
/// </summary>
public static class AcquisitionJobMessages
{
    public const string InterruptedByRestart = "Import interrupted by server restart.";
}
