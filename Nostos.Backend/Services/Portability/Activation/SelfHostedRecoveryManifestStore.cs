using System.Text;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Durable, single-writer store for retained-recovery manifests (issue #681,
/// Slice 6). Publication mirrors the activation journal: a unique temporary
/// sibling opened with <see cref="FileMode.CreateNew"/>, the whole checksummed
/// document written and flushed to disk, then a native atomic rename over
/// <c>recovery.json</c>; Linux parent directories are fsynced. The preceding
/// manifest stays authoritative if writing fails.
///
/// Reads are strict UTF-8 and strictly versioned. A manifest that exists but
/// cannot be decoded, is addressed to a different job, or contradicts its
/// directory is never treated as absent: callers receive
/// <c>migration_recovery_corrupt</c> and must fail closed (no silent restore,
/// no silent deletion). A deletion marker means cleanup already decided to
/// remove the copy; the directory is skipped instead of being re-read.
/// </summary>
internal sealed class SelfHostedRecoveryManifestStore(SelfHostedActivationPaths paths)
{
    private readonly object _writer = new();

    internal bool HasDeletionMarker(Guid id)
    {
        var marker = paths.RecoveryDeletionMarker(id);
        paths.VerifyDatabasePath(marker);
        return File.Exists(marker);
    }

    /// <summary>
    /// Durable "deleting" mark written before any retained material is removed,
    /// so an interrupted cleanup is resumed instead of being mistaken for a
    /// fresh deletion decision.
    /// </summary>
    internal void CreateDeletionMarker(Guid id)
    {
        var marker = paths.RecoveryDeletionMarker(id);
        if (File.Exists(marker)) return;
        paths.PrepareRecovery(id);
        paths.VerifyDatabasePath(marker);
        var directory = Path.GetDirectoryName(marker)!;
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write([1]);
            stream.Flush(flushToDisk: true);
        }
        ActivationFileSystem.FlushDirectory(directory);
    }

    internal SelfHostedRecoveryManifest? Read(Guid id)
    {
        var path = paths.RecoveryManifest(id);
        paths.VerifyDatabasePath(path);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw Corrupt();
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        paths.VerifyDatabasePath(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        SelfHostedRecoveryManifest manifest;
        try
        {
            manifest = SelfHostedActivationDocument.Decode<SelfHostedRecoveryManifest>(reader.ReadToEnd());
        }
        catch (DecoderFallbackException)
        {
            throw Corrupt();
        }
        catch (MigrationActivationException)
        {
            throw Corrupt();
        }

        if (manifest.JobId != id) throw Corrupt();
        return manifest;
    }

    /// <summary>
    /// Every retained recovery directory on the database volume, in job-id
    /// order. Empty directories and dirs with a deletion marker carry no
    /// readable copy. A directory that still holds retained material but has
    /// no manifest is corrupt: it cannot be listed, restored or deleted.
    /// </summary>
    internal IReadOnlyList<SelfHostedRecoveryManifest> ReadAll()
    {
        var root = paths.RecoveryRoot;
        if (!Directory.Exists(root))
        {
            if (File.Exists(root)) throw Corrupt();
            return [];
        }

        paths.VerifyDatabasePath(root);
        var result = new List<SelfHostedRecoveryManifest>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            paths.VerifyDatabasePath(directory);
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty
                || Path.GetFileName(directory) != id.ToString("N"))
                throw Corrupt();
            if (HasDeletionMarker(id)) continue;
            var manifest = Read(id);
            if (manifest is null)
            {
                if (MaterialExists(id)) throw Corrupt();
                continue;
            }

            result.Add(manifest);
        }

        return result;
    }

    internal void Write(SelfHostedRecoveryManifest manifest)
    {
        if (SelfHostedActivationDocument.Decode<SelfHostedRecoveryManifest>(
                SelfHostedActivationDocument.Encode(manifest)).JobId != manifest.JobId)
            throw Corrupt();

        lock (_writer)
        {
            paths.PrepareRecovery(manifest.JobId);
            var temporary = paths.TemporaryRecoveryManifest(manifest.JobId, Guid.NewGuid());
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                paths.VerifyDatabasePath(temporary);
                stream.Write(Encoding.UTF8.GetBytes(SelfHostedActivationDocument.Encode(manifest)));
                stream.Flush(flushToDisk: true);
            }

            paths.VerifyDatabasePath(temporary);
            paths.VerifyDatabasePath(paths.RecoveryManifest(manifest.JobId));
            ActivationFileSystem.Rename(temporary, paths.RecoveryManifest(manifest.JobId), overwrite: true);
        }
    }

    /// <summary>
    /// Removes the terminal manifest, the deletion marker and the (now empty)
    /// job directory. Call only after all retained material is conclusively
    /// gone and the retention reservation has been released. Idempotent.
    /// </summary>
    internal void DeleteEmptyDirectory(Guid id)
    {
        var directory = Path.GetDirectoryName(paths.RecoveryManifest(id))!;
        paths.VerifyDatabasePath(directory);
        var marker = paths.RecoveryDeletionMarker(id);
        if (File.Exists(marker)) File.Delete(marker);
        var manifest = paths.RecoveryManifest(id);
        if (File.Exists(manifest)) File.Delete(manifest);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    /// <summary>
    /// Removes an unfinished retention plan (temporary manifests included) when
    /// the journal proves no retained material remains. Never removes material.
    /// </summary>
    internal void DeleteUnusedPlan(Guid id)
    {
        var directory = Path.GetDirectoryName(paths.RecoveryManifest(id))!;
        if (Directory.Exists(directory))
        {
            paths.VerifyDatabasePath(directory);
            foreach (var temporary in Directory.EnumerateFiles(directory, "recovery.*.tmp"))
                File.Delete(temporary);
        }

        DeleteEmptyDirectory(id);
    }

    /// <summary>Canonical job directories under the recovery root, including ones with a deletion marker.</summary>
    internal IReadOnlyList<Guid> ListJobDirectories()
    {
        var root = paths.RecoveryRoot;
        if (!Directory.Exists(root))
        {
            if (File.Exists(root)) throw Corrupt();
            return [];
        }

        paths.VerifyDatabasePath(root);
        var result = new List<Guid>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            paths.VerifyDatabasePath(directory);
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)
                || id == Guid.Empty
                || Path.GetFileName(directory) != id.ToString("N"))
                throw Corrupt();
            result.Add(id);
        }

        return result;
    }

    internal bool MaterialExists(Guid id)
    {
        if (File.Exists(paths.PreviousDatabase(id))) return true;
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(paths.PreviousDatabase(id) + suffix)) return true;
        return Directory.Exists(paths.PreviousMedia(id));
    }

    internal static MigrationActivationException Corrupt() => new(MigrationActivationErrorCodes.RecoveryCorrupt,
        "The recovery manifest is unsupported or corrupt. The retained copy is unusable until an operator inspects it.");
}
