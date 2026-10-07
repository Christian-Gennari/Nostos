namespace Nostos.Backend.Data.Models;

/// <summary>
/// One user choice for one content provider (issue #774). The row exists only
/// when the user has explicitly turned a provider on or off; absence means "use
/// the provider's own <c>EnabledByDefault</c> declaration". That is what makes a
/// later-added provider safe by construction: it starts from its declaration,
/// never from a preference someone else set.
///
/// <see cref="ProviderId"/> is the registry id (lowercase, max 32 chars) and the
/// primary key, so there is at most one row per provider and re-enabling is an
/// upsert rather than a second row. <see cref="Enabled"/> is a real boolean, so
/// (unlike the nullable AI-provider overrides) there is no third "unset" state —
/// "unset" is the row not existing.
/// </summary>
public class ProviderPreferenceModel
{
    public string ProviderId { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
