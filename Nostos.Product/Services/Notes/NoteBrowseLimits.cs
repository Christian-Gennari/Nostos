using Nostos.Backend.Data.Interfaces;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// The two rules every note list shares (issue #1619, extracted from
/// <see cref="NoteService"/>): a page is bounded, and an unknown kind matches
/// nothing rather than everything.
/// </summary>
public static class NoteBrowseLimits
{
    /// <summary>A small page keeps the index responsive; the cap is a guard on the caller.</summary>
    public static int Clamp(int limit) => Math.Clamp(limit, 1, 200);

    /// <summary>An unknown kind matches nothing rather than everything.</summary>
    public static NoteBrowseQuery Normalized(NoteBrowseQuery query) =>
        string.IsNullOrWhiteSpace(query.Kind)
            ? query with { Kind = null }
            : query with { Kind = NoteKinds.Normalize(query.Kind) ?? "unknown" };
}
