using Microsoft.Extensions.Logging;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Sweeps scratch roots the engine itself can never reference again (issue
/// #679 Slice 11). Two families exist:
///
/// <list type="bullet">
/// <item><b>Legacy import scratch:</b> <c>POST /api/portability/import</c>
/// spools the whole archive and its staging into
/// <c>{temp}/nostos-portable-import-{guid}</c> and deletes it in <c>finally</c>.
/// A process death between create and delete leaves the tree behind forever;
/// this sweep removes generated-name trees older than the session TTL.</item>
/// <item><b>Prepared-import staging with no owner:</b> a crash between
/// <c>FilePortableImportStaging.CreateAsync</c> and recording the staging id on
/// the job leaves an unreferenced generated area. The existing
/// <see cref="FilePortableImportStagingCleanup"/> already sweeps those under
/// the transfer root; this hook exists so the same generated-area rule also
/// applies to any scratch root an operator configures for the compatibility
/// path.</item>
/// </list>
///
/// <para>Every candidate is a directory whose name exactly matches the fixed
/// prefix plus a non-empty GUID; unknown operator siblings, the root itself,
/// and linked components are never touched. A live import holds an exclusive
/// <c>import.lock</c> and is registered in-process, and the sweep skips any
/// directory with a live owner. Only unowned trees older than the cutoff — by
/// the newest timestamp of any file inside them, not the directory's own — are
/// deleted. Deletion verifies every entry individually. The sweep is
/// idempotent: a missing directory is success, and a failed delete is retried
/// on the next sweep.</para>
/// </summary>
public sealed class LegacyPortabilityScratchCleanup(
    ILogger<LegacyPortabilityScratchCleanup> logger)
{
    /// <summary>Fixed prefix used by the legacy compatibility import spool.</summary>
    public const string LegacyImportScratchPrefix = "nostos-portable-import-";

    /// <summary>Age gate: a scratch tree younger than the session TTL is never touched.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(MigrationContractLimits.SessionExpiryHours);

    /// <summary>
    /// Sweeps generated legacy scratch trees under <paramref name="scratchRoot"/>
    /// older than <paramref name="cutoffUtc"/>. The caller owns maintenance
    /// admission and the root's existence check.
    /// </summary>
    public void Sweep(string scratchRoot, DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        if (!Directory.Exists(scratchRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(scratchRoot))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!name.StartsWith(LegacyImportScratchPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = name[LegacyImportScratchPrefix.Length..];
            if (!Guid.TryParseExact(suffix, "N", out var id) || id == Guid.Empty)
            {
                continue;
            }

            try
            {
                // Ownership first: a directory with a live import (registered
                // in this process, or holding the exclusive lock from any .NET
                // process) is never a candidate, whatever its timestamps say.
                if (LegacyPortabilityScratchRegistry.IsActive(directory)
                    || LegacyPortabilityScratchLease.IsHeld(directory))
                {
                    continue;
                }

                // A previous process left no live owner. Require BOTH age
                // beyond the cutoff measured by the newest timestamp anywhere
                // inside the tree (writing a file does not update its parent
                // directory) AND the absence of a held lock.
                if (TransferPathResolver.NewestWriteTimeUtc(directory) > cutoffUtc.UtcDateTime)
                {
                    continue;
                }

                DeleteVerified(directory, ct);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or TransferPathException)
            {
                logger.LogWarning(
                    exception,
                    "Legacy portability scratch cleanup will retry {ScratchDirectory}",
                    name);
            }
        }
    }

    /// <summary>
    /// Deletes one generated tree without following links: every entry is
    /// re-resolved under the root, symlinked descendants abort the sweep for
    /// that tree, and the tree is never the root itself.
    /// </summary>
    private void DeleteVerified(string directory, CancellationToken ct)
    {
        // Legacy scratch lives in the process temp root, which is intentionally
        // outside the transfer root; VerifyScratchPath applies the same
        // descendant rule against the tree the caller selected.
        var root = Path.GetFullPath(directory);
        VerifyDescendant(root, directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            var verified = Path.GetFullPath(entry);
            VerifyDescendant(root, verified);
            if (Directory.Exists(verified))
            {
                DeleteVerified(verified, ct);
            }
            else
            {
                File.Delete(verified);
            }
        }

        Directory.Delete(root);
    }

    private static void VerifyDescendant(string root, string candidate)
    {
        var full = Path.GetFullPath(candidate);
        if (!string.Equals(full, root, StringComparison.Ordinal)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new TransferPathException(
                TransferPathException.OutsideRoot,
                "A legacy portability scratch path escaped its generated tree.");
        }

        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TransferPathException(
                TransferPathException.ReparsePoint,
                "A legacy portability scratch path is a symlink or reparse point.");
        }
    }
}
