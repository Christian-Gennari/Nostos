namespace Nostos.Backend.Data.Models;

public class BackupRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long SizeBytes { get; set; }
    public string Provider { get; set; } = BackupProvider.Local;
    public string Status { get; set; } = BackupStatus.InProgress;
    public string? LocalArchivePath { get; set; }
    public string? ManifestJson { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IncludeBookFiles { get; set; }
}

public static class BackupProvider
{
    public const string Local = "Local";
}

public static class BackupStatus
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}