using Nostos.Backend.Data.Models;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>Writings and the writing-to-note pairs, including added-at.</summary>
internal static class PortableWritingComparisons
{
    internal static readonly IReadOnlyList<ComparisonEntry<WritingContext>> WritingComparisons =
    [
        Entry<WritingContext>(
            nameof(PortableWriting.Id),
            Ef(("WritingModel", nameof(WritingModel.Id))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Id), context.Row.Id, context.Expected.Id)),
        Entry<WritingContext>(
            nameof(PortableWriting.Name),
            Ef(("WritingModel", nameof(WritingModel.Name))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Name), context.Row.Name, context.Expected.Name)),
        Entry<WritingContext>(
            nameof(PortableWriting.Type),
            Ef(("WritingModel", nameof(WritingModel.Type))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Type), context.Row.Type.ToString(), context.Expected.Type, ignoreCase: true)),
        Entry<WritingContext>(
            nameof(PortableWriting.Content),
            Ef(("WritingModel", nameof(WritingModel.Content))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Content), context.Row.Content, context.Expected.Content)),
        Entry<WritingContext>(
            nameof(PortableWriting.ParentId),
            Ef(("WritingModel", nameof(WritingModel.ParentId))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.ParentId), context.Row.ParentId, context.Expected.ParentId)),
        Entry<WritingContext>(
            nameof(PortableWriting.CreatedAt),
            Ef(("WritingModel", nameof(WritingModel.CreatedAt))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
        Entry<WritingContext>(
            nameof(PortableWriting.UpdatedAt),
            Ef(("WritingModel", nameof(WritingModel.UpdatedAt))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.UpdatedAt), context.Row.UpdatedAt, context.Expected.UpdatedAt)),
        Entry<WritingContext>(
            nameof(PortableWriting.PageSetup),
            Ef(("WritingModel", nameof(WritingModel.PageSetup))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.PageSetup), context.Row.PageSetup, context.Expected.PageSetup)),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<WritingNoteContext>> WritingNoteComparisons =
    [
        PairEntry<WritingNoteContext>(
            nameof(PortableWritingNote.WritingId),
            nameof(PortableWritingNote.NoteId),
            Ef(
                ("WritingNoteModel", nameof(WritingNoteModel.WritingId)),
                ("WritingNoteModel", nameof(WritingNoteModel.NoteId))),
            (context, failures) => CompareJoinPairs(
                failures,
                "writingNote",
                $"{nameof(PortableWritingNote.WritingId)},{nameof(PortableWritingNote.NoteId)}",
                context.Expected.Select(item => (item.WritingId, item.NoteId)),
                context.Rows.Select(row => (row.WritingId, row.NoteId)))),
        Entry<WritingNoteContext>(
            nameof(PortableWritingNote.AddedAt),
            Ef(("WritingNoteModel", nameof(WritingNoteModel.AddedAt))),
            (context, failures) =>
            {
                var expected = context.Expected
                    .GroupBy(item => (item.WritingId, item.NoteId))
                    .ToDictionary(group => group.Key, group => group.First().AddedAt);
                CompareAddedAt(
                    failures,
                    "writingNote",
                    nameof(PortableWritingNote.AddedAt),
                    expected,
                    context.Rows.Select(row => ((row.WritingId, row.NoteId), row.AddedAt)));
            }),
    ];

    internal static void CompareWritings(
        PortableLibraryData data,
        List<WritingModel> writings,
        FailureList failures)
    {
        var expected = data.Writings.ToDictionary(item => item.Id);
        var actual = writings.ToDictionary(item => item.Id);
        CompareIdSets("writing", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(WritingComparisons, new WritingContext(row, item), failures);
            }
        }
    }

    internal static void CompareWritingNotes(
        PortableLibraryData data,
        List<WritingNoteModel> writingNotes,
        FailureList failures) =>
        Run(
            WritingNoteComparisons,
            new WritingNoteContext(writingNotes, data.WritingNotes ?? []),
            failures);
}
