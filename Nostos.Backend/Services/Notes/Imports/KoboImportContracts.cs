namespace Nostos.Backend.Services.Notes.Imports;

internal sealed record KoboVolume(
    string VolumeId,
    string? Title,
    string? Author,
    string? Isbn,
    IReadOnlyList<KoboBookmark> Bookmarks);

internal sealed record KoboBookmark(
    string BookmarkId,
    string? Text,
    string? Annotation);
