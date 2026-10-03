namespace Nostos.Backend.Data.Models;

/// <summary>
/// Singleton row holding the server-wide assistant settings the owner chooses
/// once (issue #262 §7). Today it holds exactly one thing: the post-processing
/// mode applied when the user's own words become a note.
///
/// Nostos is a single-user, unauthenticated app, so the setting is server-wide
/// and there is exactly one row. <see cref="CaptureProcessingMode"/> is nullable
/// on purpose: <c>NULL</c> means "never chosen", which must stay distinguishable
/// from a stored <c>verbatim</c>. The effective value is resolved through
/// <c>ThoughtProcessingModes.Normalize</c>, so "nothing stored" reads back as
/// <c>verbatim</c> without pretending a choice was ever made.
/// </summary>
public class AssistantSettingsModel
{
    /// <summary>The fixed primary key of the one settings row.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The stored capture post-processing mode, or null when the owner has never
    /// chosen. Never returned raw: callers normalise it through
    /// <c>ThoughtProcessingModes</c>.
    /// </summary>
    public string? CaptureProcessingMode { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
