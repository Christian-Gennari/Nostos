using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;
using static Nostos.Backend.Services.Portability.Verification.PortableBookComparisons;
using static Nostos.Backend.Services.Portability.Verification.PortableProgressComparisons;
using static Nostos.Backend.Services.Portability.Verification.PortableCollectionComparisons;
using static Nostos.Backend.Services.Portability.Verification.PortableNoteComparisons;
using static Nostos.Backend.Services.Portability.Verification.PortableWritingComparisons;
using static Nostos.Backend.Services.Portability.Verification.PortableSettingsComparisons;
using static Nostos.Backend.Services.Portability.Verification.PortableReadingListComparisons;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>
/// The coverage catalog: every portable archive record maps to the executable
/// comparison tables that verify it, or to a reasoned exclusion. A new record
/// or property fails the coverage test until it is registered here.
/// </summary>
internal static class PortableComparisonCatalog
{
    internal static readonly IReadOnlyDictionary<string, string> LibraryDataExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableLibraryData.Works)] = "Container; every PortableWork element is compared by the PortableWork and WorkModel comparison table.",
            [nameof(PortableLibraryData.Books)] = "Container; every PortableBook element is compared by the PortableBook comparison table.",
            [nameof(PortableLibraryData.Collections)] = "Container; every PortableCollection element is compared by the PortableCollection comparison table.",
            [nameof(PortableLibraryData.BookCollections)] = "Container; every PortableBookCollection element is compared by the PortableBookCollection comparison table.",
            [nameof(PortableLibraryData.Notes)] = "Container; every PortableNote element is compared by the PortableNote comparison table.",
            [nameof(PortableLibraryData.Topics)] = "Container; every PortableTopic element is compared by the PortableTopic comparison table.",
            [nameof(PortableLibraryData.NoteTopics)] = "Container; every PortableNoteTopic element is compared by the PortableNoteTopic comparison table.",
            [nameof(PortableLibraryData.Writings)] = "Container; every PortableWriting element is compared by the PortableWriting comparison table.",
            [nameof(PortableLibraryData.BookAcquisitions)] = "Container; every PortableBookAcquisition element is compared by the PortableBookAcquisition comparison table.",
            [nameof(PortableLibraryData.AssistantSettings)] = "Singleton; presence and both fields are compared by CompareAssistantSettings and the PortableAssistantSettings comparison table.",
            [nameof(PortableLibraryData.WritingNotes)] = "Container; every PortableWritingNote element is compared by the PortableWritingNote comparison table.",
            [nameof(PortableLibraryData.NoteImportBookLinks)] = "Container; every PortableNoteImportBookLink element is compared by the PortableNoteImportBookLink comparison table.",
            [nameof(PortableLibraryData.ReadingListEntries)] = "Container; every PortableReadingListEntry element is compared by the PortableReadingListEntry comparison table.",
        };

    internal static readonly IReadOnlyDictionary<string, string> ManifestExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableArchiveManifest.ExportedAtUtc)] =
                "Exporter timestamp metadata is not portable library state and is not recreated by activation.",
            [nameof(PortableArchiveManifest.ApplicationVersion)] =
                "Exporter product version metadata is informational and is not portable library state.",
        };

    internal static readonly IReadOnlyDictionary<string, string> MediaEntryExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableArchiveMediaEntry.ContentType)] =
                "Content type is HTTP response metadata; it is not persisted into the candidate library and is not portable library state.",
        };

    internal static readonly IReadOnlyDictionary<string, string> EmptyExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A new portable archive record or property makes the archive-coverage test
    /// fail until it is registered here (or given a reasoned exclusion), and
    /// registering it requires an executable comparison delegate.
    /// </summary>
    internal static IReadOnlyDictionary<string, RecordComparisonSpec> Specs { get; } =
        BuildSpecs();

    internal static IReadOnlyDictionary<string, RecordComparisonSpec> BuildSpecs() =>
        new Dictionary<string, RecordComparisonSpec>(StringComparer.Ordinal)
        {
            [nameof(PortableLibraryData)] = new(
                [nameof(PortableLibraryData.Version)],
                NoFields,
                LibraryDataExclusions),
            [nameof(PortableWork)] = Spec(WorkComparisons),
            [nameof(PortableBook)] = Spec(BookComparisons),
            [nameof(PortableBookMetadata)] = Spec(BookMetadataComparisons),
            [nameof(PortableReadingProgress)] = Spec(BookProgressComparisons),
            [nameof(PortableCollection)] = Spec(CollectionComparisons),
            [nameof(PortableBookCollection)] = Spec(BookCollectionComparisons),
            [nameof(PortableNote)] = Spec(NoteComparisons),
            [nameof(PortableTopic)] = Spec(TopicComparisons),
            [nameof(PortableNoteTopic)] = Spec(NoteTopicComparisons),
            [nameof(PortableWriting)] = Spec(WritingComparisons),
            [nameof(PortableWritingNote)] = Spec(WritingNoteComparisons),
            [nameof(PortableBookAcquisition)] = Spec(AcquisitionComparisons),
            [nameof(PortableAssistantSettings)] = Spec(AssistantComparisons),
            [nameof(PortableNoteImportBookLink)] = Spec(ImportLinkComparisons),
            [nameof(PortableReadingListEntry)] = Spec(ReadingListComparisons),
            [nameof(PortableArchiveManifest)] = new(
                [
                    nameof(PortableArchiveManifest.Format),
                    nameof(PortableArchiveManifest.FormatVersion),
                    nameof(PortableArchiveManifest.DataVersion),
                    nameof(PortableArchiveManifest.Counts),
                    nameof(PortableArchiveManifest.Data),
                    nameof(PortableArchiveManifest.Media),
                ],
                NoFields,
                ManifestExclusions),
            [nameof(PortableArchiveCounts)] = new(
                PortableVerificationCounts.ManifestCountProperties,
                NoFields,
                EmptyExclusions),
            [nameof(PortableArchivePayload)] = new(
                [
                    nameof(PortableArchivePayload.Path),
                    nameof(PortableArchivePayload.Length),
                    nameof(PortableArchivePayload.Sha256),
                ],
                NoFields,
                EmptyExclusions),
            [nameof(PortableArchiveMediaEntry)] = new(
                [
                    nameof(PortableArchiveMediaEntry.BookId),
                    nameof(PortableArchiveMediaEntry.Kind),
                    nameof(PortableArchiveMediaEntry.Path),
                    nameof(PortableArchiveMediaEntry.FileName),
                    nameof(PortableArchiveMediaEntry.Length),
                    nameof(PortableArchiveMediaEntry.Sha256),
                ],
                NoFields,
                MediaEntryExclusions),
        };

    internal static RecordComparisonSpec Spec<TContext>(
        IReadOnlyList<ComparisonEntry<TContext>> entries) =>
        new(
            entries.SelectMany(entry => entry.ArchiveProperties).ToArray(),
            entries.SelectMany(entry => entry.CandidateFields).ToArray(),
            EmptyExclusions);
}
