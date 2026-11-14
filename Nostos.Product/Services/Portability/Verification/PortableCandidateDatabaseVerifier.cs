using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>
/// Loads the candidate database with read-only, provider-neutral queries and
/// hands each portable kind to its comparison domain in a fixed order, so the
/// report a candidate produces does not depend on which domain runs first.
/// </summary>
internal static class PortableCandidateDatabaseVerifier
{
    /// <summary>
    /// The one relational candidate comparison shared by
    /// <see cref="VerifyCandidateAsync"/> and
    /// <see cref="VerifyCandidateDatabaseAsync"/>. It reads the supplied context
    /// with read-only, provider-neutral LINQ queries only: no raw SQL, no PRAGMA and
    /// no row identifiers, so any EF provider can serve the comparison. The caller
    /// owns media verification and adds that dimension's count facts.
    /// </summary>
    internal static async Task<CandidateDatabaseComparison> CompareAsync(
        NostosDbContext candidateDatabase,
        PortableLibraryData data,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        CancellationToken ct)
    {
        var failures = new FailureList();
        var mediaByKey = descriptors
            .GroupBy(descriptor => (descriptor.BookId, descriptor.Kind))
            .ToDictionary(group => group.Key, group => group.First());

        var works = await candidateDatabase.Works.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var books = await candidateDatabase.Books.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var collections = await candidateDatabase.Collections.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var memberships = await candidateDatabase.BookCollections.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var notes = await candidateDatabase.Notes.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var topics = await candidateDatabase.Topics.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var noteTopics = await candidateDatabase.NoteTopics.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var writings = await candidateDatabase.Writings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var writingNotes = await candidateDatabase.WritingNotes.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var acquisitions = await candidateDatabase.BookAcquisitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var importLinks = await candidateDatabase.NoteImportBookLinks.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var readingList = await candidateDatabase.ReadingListEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var assistantSettings = await candidateDatabase.AssistantSettings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        PortableBookComparisons.CompareWorks(data, works, failures);
        PortableBookComparisons.CompareBooks(data, books, mediaByKey, failures);
        PortableCollectionComparisons.CompareCollections(data, collections, failures);
        PortableCollectionComparisons.CompareBookCollections(data, memberships, failures);
        PortableNoteComparisons.CompareNotes(data, notes, failures);
        PortableNoteComparisons.CompareTopics(data, topics, failures);
        PortableNoteComparisons.CompareNoteTopics(data, noteTopics, failures);
        PortableWritingComparisons.CompareWritings(data, writings, failures);
        PortableWritingComparisons.CompareWritingNotes(data, writingNotes, failures);
        PortableSettingsComparisons.CompareAcquisitions(data, acquisitions, failures);
        PortableSettingsComparisons.CompareNoteImportBookLinks(data, importLinks, failures);
        PortableReadingListComparisons.CompareReadingListEntries(data, readingList, failures);
        PortableSettingsComparisons.CompareAssistantSettings(data, assistantSettings, failures);

        var rowsVerified =
            works.Count + books.Count + collections.Count + memberships.Count + notes.Count + topics.Count
            + noteTopics.Count + writings.Count + writingNotes.Count + acquisitions.Count + importLinks.Count
            + readingList.Count + assistantSettings.Count;

        return new CandidateDatabaseComparison(
            failures,
            new CandidateRowCounts(
                works.Count,
                books.Count,
                collections.Count,
                memberships.Count,
                notes.Count,
                topics.Count,
                noteTopics.Count,
                writings.Count,
                writingNotes.Count,
                acquisitions.Count,
                importLinks.Count,
                assistantSettings.Count,
                readingList.Count),
            rowsVerified);
    }
}
