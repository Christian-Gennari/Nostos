namespace Nostos.Backend.Services.Notes.Imports;

public sealed record KoreaderImportReport(
    string Status,
    Guid? BookId,
    string? BookTitle,
    int AnnotationCount,
    int ImportedCount,
    int DuplicateCount,
    int SkippedCount,
    string? Message);

internal sealed record KoreaderMetadataDocument(
    string? Title,
    string? Author,
    string? Isbn,
    IReadOnlyList<KoreaderAnnotation> Annotations);

internal sealed record KoreaderAnnotation(
    string? Text,
    string? Note,
    string? Chapter,
    string? DateTime,
    string? Page,
    string? PageNumber,
    string? PositionStart,
    string? PositionEnd);
