using System.Text.Json;
using Nostos.Backend.Services.Knowledge;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Pure projection from canonical assistant retrieval results into the bounded
/// evidence/source references returned by an assistant turn.
///
/// Stable handles retain canonical identity. Labels, excerpts and source
/// locators are presentation data derived only from successful server-owned
/// retrieval results.
/// </summary>
internal static class AssistantEvidenceProjection
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static void MergeEvidenceReferences(
        List<AssistantEvidenceReferenceDto> target,
        IEnumerable<AssistantEvidenceReferenceDto> incoming,
        int maxEvidenceArtifacts)
    {
        foreach (var evidence in incoming)
        {
            if (target.Count >= maxEvidenceArtifacts)
                return;

            var key = EvidenceKey(evidence.Handle);
            if (target.Any(existing => string.Equals(
                    EvidenceKey(existing.Handle),
                    key,
                    StringComparison.Ordinal)))
                continue;

            target.Add(evidence);
        }
    }

    public static IEnumerable<AssistantEvidenceReferenceDto> ExtractKnowledgeSearchEvidence(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var note in response.Notes)
        {
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(note.Handle),
                string.IsNullOrWhiteSpace(note.BookTitle) ? "Note" : $"Note · {note.BookTitle}",
                ClipEvidence(note.Snippet),
                BookTitle: note.BookTitle);
        }

        foreach (var topic in response.Topics)
        {
            var excerpt = topic.MatchSnippet
                ?? topic.SupportingNotes.FirstOrDefault()?.Snippet;
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(topic.Handle),
                topic.Name,
                ClipEvidence(excerpt));
        }

        foreach (var passage in response.BookPassages)
            yield return ToEvidenceReference(passage);
    }

    public static IEnumerable<AssistantEvidenceReferenceDto> ExtractNoteListForBookEvidence(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Array } element)
            yield break;

        List<NoteDto>? notes;
        try
        {
            notes = JsonSerializer.Deserialize<List<NoteDto>>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (notes is null)
            yield break;

        foreach (var note in notes)
        {
            yield return new AssistantEvidenceReferenceDto(
                new AssistantEvidenceHandleDto(
                    KnowledgeEvidenceKinds.Note,
                    NoteId: note.Id),
                string.IsNullOrWhiteSpace(note.BookTitle) ? "Note" : $"Note · {note.BookTitle}",
                ClipEvidence(note.SelectedText ?? note.Content),
                BookTitle: note.BookTitle);
        }
    }

    public static IEnumerable<AssistantEvidenceReferenceDto> ExtractNoteSearchEvidence(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Array } element)
            yield break;

        List<NoteSearchHitDto>? notes;
        try
        {
            notes = JsonSerializer.Deserialize<List<NoteSearchHitDto>>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (notes is null)
            yield break;

        foreach (var note in notes)
        {
            yield return new AssistantEvidenceReferenceDto(
                new AssistantEvidenceHandleDto(
                    KnowledgeEvidenceKinds.Note,
                    NoteId: note.Id),
                string.IsNullOrWhiteSpace(note.BookTitle) ? "Note" : $"Note · {note.BookTitle}",
                ClipEvidence(note.Snippet ?? note.SelectedText ?? note.Content),
                BookTitle: note.BookTitle);
        }
    }

    public static IEnumerable<AssistantEvidenceReferenceDto> ExtractKnowledgeReadEvidence(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeReadResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeReadResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        if (response.Note is { } note)
        {
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(response.Handle),
                string.IsNullOrWhiteSpace(note.BookTitle) ? "Note" : $"Note · {note.BookTitle}",
                ClipEvidence(note.SelectedText ?? note.Content),
                BookTitle: note.BookTitle);
        }
        else if (response.Topic is { } topic)
        {
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(response.Handle),
                topic.Name,
                ClipEvidence(topic.Notes.FirstOrDefault()?.Snippet));
        }
        else if (response.BookPassage is { } passage)
        {
            yield return ToEvidenceReference(passage);
        }
    }

    public static IEnumerable<AssistantEvidenceReferenceDto> ExtractBookTextEvidence(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        BookTextSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<BookTextSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var passage in response.Passages)
        {
            var handle = new AssistantEvidenceHandleDto(
                KnowledgeEvidenceKinds.BookText,
                BookId: passage.BookId,
                SourceSha256: passage.SourceSha256,
                ExtractorVersion: passage.ExtractorVersion,
                Ordinal: passage.Ordinal);
            var source = ToAssistantSource(
                passage.BookId,
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format,
                passage.SourceSha256,
                passage.Text,
                passage.SourceSegments);

            yield return new AssistantEvidenceReferenceDto(
                handle,
                passage.BookTitle,
                ClipEvidence(passage.Text),
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format.ToString().ToLowerInvariant(),
                source?.Locators ?? []);
        }
    }

    public static IEnumerable<AssistantSourceReferenceDto> ExtractKnowledgeSearchSources(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var passage in response.BookPassages)
        {
            if (ToAssistantSource(
                passage.BookId,
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format,
                passage.SourceSha256,
                passage.Text,
                passage.SourceSegments) is { } source)
            {
                yield return source;
            }
        }
    }

    public static IEnumerable<AssistantSourceReferenceDto> ExtractKnowledgeReadSources(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeReadResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeReadResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response?.BookPassage is not { } passage)
            yield break;

        if (ToAssistantSource(
            passage.BookId,
            passage.BookTitle,
            passage.BookAuthor,
            passage.Format,
            passage.SourceSha256,
            passage.Text,
            passage.SourceSegments) is { } source)
        {
            yield return source;
        }
    }

    public static IEnumerable<AssistantSourceReferenceDto> ExtractBookTextSources(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        BookTextSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<BookTextSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var passage in response.Passages)
        {
            if (ToAssistantSource(
                passage.BookId,
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format,
                passage.SourceSha256,
                passage.Text,
                passage.SourceSegments) is { } source)
            {
                yield return source;
            }
        }
    }

    private static string EvidenceKey(AssistantEvidenceHandleDto handle) =>
        string.Join(
            "|",
            handle.Kind,
            handle.NoteId,
            handle.TopicId,
            handle.BookId,
            handle.SourceSha256,
            handle.ExtractorVersion,
            handle.Ordinal);

    private static AssistantEvidenceHandleDto ToEvidenceHandle(KnowledgeEvidenceHandle handle) =>
        new(
            handle.Kind,
            handle.NoteId,
            handle.TopicId,
            handle.BookId,
            handle.SourceSha256,
            handle.ExtractorVersion,
            handle.Ordinal);

    private static AssistantEvidenceReferenceDto ToEvidenceReference(KnowledgeBookEvidence passage)
    {
        var source = ToAssistantSource(
            passage.BookId,
            passage.BookTitle,
            passage.BookAuthor,
            passage.Format,
            passage.SourceSha256,
            passage.Text,
            passage.SourceSegments);

        return new AssistantEvidenceReferenceDto(
            ToEvidenceHandle(passage.Handle),
            passage.BookTitle,
            ClipEvidence(passage.Text),
            passage.BookTitle,
            passage.BookAuthor,
            passage.Format.ToString().ToLowerInvariant(),
            source?.Locators ?? []);
    }

    private static string? ClipEvidence(string? value, int max = 320)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var flat = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }

    private static AssistantSourceReferenceDto? ToAssistantSource(
        Guid bookId,
        string bookTitle,
        string? bookAuthor,
        BookTextSourceFormat format,
        string sourceSha256,
        string text,
        IReadOnlyList<BookTextSourceSegment> sourceSegments)
    {
        var locators = sourceSegments
            .Select(segment => segment.Locator switch
            {
                PdfBookTextSourceLocator pdf => new AssistantSourceLocatorDto(
                    Type: "pdf",
                    PdfPageIndex: pdf.PageIndex,
                    PdfPageLabel: pdf.PageLabel,
                    StartTextOffset: pdf.StartTextOffset,
                    EndTextOffset: pdf.EndTextOffset),
                EpubBookTextSourceLocator epub => new AssistantSourceLocatorDto(
                    Type: "epub",
                    EpubSpineIndex: epub.SpineIndex,
                    EpubResourceHref: epub.ResourceHref,
                    EpubCfi: epub.Cfi,
                    StartTextOffset: epub.StartTextOffset,
                    EndTextOffset: epub.EndTextOffset),
                AudioBookTextSourceLocator audio => new AssistantSourceLocatorDto(
                    Type: "audio",
                    StartTextOffset: checked((int)Math.Min(int.MaxValue, audio.StartMs)),
                    EndTextOffset: checked((int)Math.Min(int.MaxValue, audio.EndMs))),
                _ => null,
            })
            .Where(locator => locator is not null)
            .Cast<AssistantSourceLocatorDto>()
            .ToList();

        if (locators.Count == 0)
            return null;

        return new AssistantSourceReferenceDto(
            bookId,
            bookTitle,
            bookAuthor,
            format.ToString().ToLowerInvariant(),
            sourceSha256,
            text,
            locators);
    }
}
