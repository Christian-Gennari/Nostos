namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Terminal cleanup for an empty-destination cutover. The old empty components
/// are used as rollback scratch exactly like a populated replacement, but the
/// plan deliberately keeps no seven-day recovery copy for an empty library, so
/// a committed roll-forward deletes the retained scratch. Runs after the
/// component steps (order 30), so it only ever removes previous material once
/// both live components are the activated generation. Idempotent: a repeated
/// pass finds nothing and succeeds.
/// </summary>
internal sealed class SelfHostedActivationEmptyRetentionStep(
    SelfHostedActivationPaths paths,
    SelfHostedRecoveryManifestStore manifests) : ISelfHostedActivationRecoveryStep
{
    public int Order => 30;

    public void Validate(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action)
    {
        if (action != SelfHostedRecoveryAction.RollForwardCandidate || journal.RetainPreviousLibrary) return;
        paths.Verify(journal.JobId);
    }

    public void Execute(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action)
    {
        if (action != SelfHostedRecoveryAction.RollForwardCandidate || journal.RetainPreviousLibrary) return;
        if (!File.Exists(paths.LiveDatabase) || !Directory.Exists(paths.LiveMedia))
            throw SelfHostedActivationPaths.Failure(
                "An empty-destination activation cannot discard its scratch before the activated library is live.");

        var media = paths.PreviousMedia(journal.JobId);
        paths.VerifyMediaPath(media);
        if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
        else if (File.Exists(media)) File.Delete(media);

        var database = paths.PreviousDatabase(journal.JobId);
        paths.VerifyDatabasePath(database);
        foreach (var path in new[] { database, database + "-wal", database + "-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }

        manifests.DeleteEmptyDirectory(journal.JobId);
    }
}
