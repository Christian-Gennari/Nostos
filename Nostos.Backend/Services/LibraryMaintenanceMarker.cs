using System.Text.Json;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;

namespace Nostos.Backend.Services;

/// <summary>
/// Advisory marker beside the configured DB, outside SQLite. It never changes live
/// DB/media. Journal reconciliation, not this marker, decides generation recovery.
/// </summary>
public sealed class LibraryMaintenanceMarker(string databasePath)
{
    private readonly string _root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, ".nostos-activation");
    public string MarkerPath => Path.Combine(_root, "maintenance.json");

    public void Write(LibraryMaintenanceReason reason)
    {
        Directory.CreateDirectory(_root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, reason });
        var temporary = MarkerPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, MarkerPath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        if (File.Exists(MarkerPath + ".tmp")) File.Delete(MarkerPath + ".tmp");
    }

    public void RecoverStaleMarker()
    {
        // No filesystem cutover executor exists in Slices 1/2. Never erase a
        // marker and bootstrap a mixed library when a later slice's journal needs
        // reconciliation. Scan only generated job directories, without following links.
        if (Directory.Exists(_root))
        {
            RefuseLink(_root);
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                RefuseLink(directory);
                var journalPath = Path.Combine(directory, "activation.json");
                if (!File.Exists(journalPath)) continue;
                RefuseLink(journalPath);
                var action = SelfHostedActivationDocument.RecoveryAction(File.ReadAllText(journalPath));
                if (action != SelfHostedRecoveryAction.Nothing)
                    throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryFailed,
                        "An activation journal requires reconciliation before startup.");
            }
        }
        Clear();
    }

    private static void RefuseLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryFailed,
                "The maintenance state location cannot be a link.");
    }
}
