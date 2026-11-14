using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>Notes, the topic vocabulary and the note-to-topic pairs.</summary>
internal static class PortableNoteComparisons
{
    internal static readonly IReadOnlyList<ComparisonEntry<NoteContext>> NoteComparisons =
    [
        Entry<NoteContext>(
            nameof(PortableNote.Id),
            Ef(("NoteModel", nameof(NoteModel.Id))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.Id), context.Row.Id, context.Expected.Id)),
        Entry<NoteContext>(
            nameof(PortableNote.BookId),
            Ef(("NoteModel", nameof(NoteModel.BookId))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.BookId), context.Row.BookId, context.Expected.BookId)),
        Entry<NoteContext>(
            nameof(PortableNote.Content),
            Ef(("NoteModel", nameof(NoteModel.Content))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.Content), context.Row.Content, context.Expected.Content)),
        Entry<NoteContext>(
            nameof(PortableNote.CfiRange),
            Ef(("NoteModel", nameof(NoteModel.CfiRange))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.CfiRange), context.Row.CfiRange, context.Expected.CfiRange)),
        Entry<NoteContext>(
            nameof(PortableNote.SelectedText),
            Ef(("NoteModel", nameof(NoteModel.SelectedText))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.SelectedText), context.Row.SelectedText, context.Expected.SelectedText)),
        Entry<NoteContext>(
            nameof(PortableNote.CreatedAt),
            Ef(("NoteModel", nameof(NoteModel.CreatedAt))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
        Entry<NoteContext>(
            nameof(PortableNote.RawContent),
            Ef(("NoteModel", nameof(NoteModel.RawContent))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.RawContent), context.Row.RawContent, context.Expected.RawContent)),
        Entry<NoteContext>(
            nameof(PortableNote.CaptureSource),
            Ef(("NoteModel", nameof(NoteModel.CaptureSource))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.CaptureSource), context.Row.CaptureSource, context.Expected.CaptureSource)),
        Entry<NoteContext>(
            nameof(PortableNote.ProcessingMode),
            Ef(("NoteModel", nameof(NoteModel.ProcessingMode))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.ProcessingMode), context.Row.ProcessingMode, context.Expected.ProcessingMode)),
        Entry<NoteContext>(
            nameof(PortableNote.SourceAnchorKind),
            Ef(("NoteModel", nameof(NoteModel.SourceAnchorKind))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.SourceAnchorKind), context.Row.SourceAnchorKind, context.Expected.SourceAnchorKind)),
        Entry<NoteContext>(
            nameof(PortableNote.SourceAnchorValue),
            Ef(("NoteModel", nameof(NoteModel.SourceAnchorValue))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.SourceAnchorValue), context.Row.SourceAnchorValue, context.Expected.SourceAnchorValue)),
        Entry<NoteContext>(
            nameof(PortableNote.AnchorVerified),
            Ef(("NoteModel", nameof(NoteModel.AnchorVerified))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.AnchorVerified), context.Row.AnchorVerified, context.Expected.AnchorVerified)),
        Entry<NoteContext>(
            nameof(PortableNote.HighlightColour),
            Ef(("NoteModel", nameof(NoteModel.HighlightColour))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.HighlightColour), context.Row.HighlightColour, context.Expected.HighlightColour)),
        Entry<NoteContext>(
            nameof(PortableNote.Kind),
            Ef(("NoteModel", nameof(NoteModel.Kind))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.Kind), context.Row.Kind, context.Expected.Kind ?? NoteKinds.Note)),
        Entry<NoteContext>(
            nameof(PortableNote.PassageTitle),
            Ef(("NoteModel", nameof(NoteModel.PassageTitle))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.PassageTitle), context.Row.PassageTitle, context.Expected.PassageTitle)),
        Entry<NoteContext>(
            nameof(PortableNote.PassageEnd),
            Ef(("NoteModel", nameof(NoteModel.PassageEnd))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.PassageEnd), context.Row.PassageEnd, context.Expected.PassageEnd)),
        Entry<NoteContext>(
            nameof(PortableNote.PassageHtml),
            Ef(("NoteModel", nameof(NoteModel.PassageHtml))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.PassageHtml), context.Row.PassageHtml, context.Expected.PassageHtml)),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<TopicContext>> TopicComparisons =
    [
        Entry<TopicContext>(
            nameof(PortableTopic.Id),
            Ef(("TopicModel", nameof(TopicModel.Id))),
            (context, failures) => CompareField(failures, "topic", context.Row.Id, nameof(PortableTopic.Id), context.Row.Id, context.Expected.Id)),
        Entry<TopicContext>(
            nameof(PortableTopic.Topic),
            Ef(("TopicModel", nameof(TopicModel.Topic))),
            (context, failures) => CompareField(failures, "topic", context.Row.Id, nameof(PortableTopic.Topic), context.Row.Topic, context.Expected.Topic)),
        Entry<TopicContext>(
            nameof(PortableTopic.Description),
            Ef(("TopicModel", nameof(TopicModel.Description))),
            (context, failures) => CompareField(failures, "topic", context.Row.Id, nameof(PortableTopic.Description), context.Row.Description, context.Expected.Description)),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<NoteTopicContext>> NoteTopicComparisons =
    [
        PairEntry<NoteTopicContext>(
            nameof(PortableNoteTopic.NoteId),
            nameof(PortableNoteTopic.TopicId),
            Ef(
                ("NoteTopicModel", nameof(NoteTopicModel.NoteId)),
                ("NoteTopicModel", nameof(NoteTopicModel.TopicId))),
            (context, failures) => CompareJoinPairs(
                failures,
                "noteTopic",
                $"{nameof(PortableNoteTopic.NoteId)},{nameof(PortableNoteTopic.TopicId)}",
                context.Expected.Select(item => (item.NoteId, item.TopicId)),
                context.Rows.Select(row => (row.NoteId, row.TopicId)))),
    ];

    internal static void CompareNotes(
        PortableLibraryData data,
        List<NoteModel> notes,
        FailureList failures)
    {
        var expected = data.Notes.ToDictionary(item => item.Id);
        var actual = notes.ToDictionary(item => item.Id);
        CompareIdSets("note", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(NoteComparisons, new NoteContext(row, item), failures);
            }
        }
    }

    internal static void CompareTopics(
        PortableLibraryData data,
        List<TopicModel> topics,
        FailureList failures)
    {
        var expected = data.Topics.ToDictionary(item => item.Id);
        var actual = topics.ToDictionary(item => item.Id);
        CompareIdSets("topic", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(TopicComparisons, new TopicContext(row, item), failures);
            }
        }
    }

    internal static void CompareNoteTopics(
        PortableLibraryData data,
        List<NoteTopicModel> noteTopics,
        FailureList failures) =>
        Run(
            NoteTopicComparisons,
            new NoteTopicContext(noteTopics, data.NoteTopics),
            failures);
}
