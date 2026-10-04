// Nostos.Backend.Tests/Portability/PortableCompletenessInventoryTests.cs

using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableCompletenessInventoryTests
{
    private const string NavigationReason =
        "Relationship navigation only; the portable relationship is represented by classified scalar keys/join entities.";

    private static readonly IReadOnlyDictionary<string, EntityClassification> Inventory =
        new Dictionary<string, EntityClassification>(StringComparer.Ordinal)
        {
            ["BookModel"] = PortableEntity(
                portable:
                [
                    "Id",
                    "WorkId",
                    "Status",
                    "StatusMessage",
                    "Title",
                    "Author",
                    "CreatedAt",
                ],
                excluded:
                [
                    Excluded(
                        "BookType",
                        "EF Core TPH inheritance discriminator ('physical', 'ebook', 'audiobook'); represented in archive by explicit PortableBook.Type field."),
                    Excluded(
                        "NormalizedIsbn",
                        "Derived search optimization index column; recomputed from Isbn on import/save."),
                    Excluded(
                        "NormalizedAsin",
                        "Derived search optimization index column; recomputed from Asin on import/save."),
                ],
                excludedNavigations:
                [
                    Excluded("Work", NavigationReason),
                    Excluded("BookCollections", NavigationReason),
                    Excluded("Acquisition", NavigationReason),
                    Excluded(
                        "FileDetails",
                        "EF Core owned entity navigation; members are classified in FileInfoDetails."),
                    Excluded(
                        "Metadata",
                        "EF Core owned entity navigation; members are classified in BookMetadata."),
                    Excluded(
                        "Progress",
                        "EF Core owned entity navigation; members are classified in ReadingProgress."),
                ]),

            ["PhysicalBookModel"] = PortableEntity(
                portable:
                [
                    "Isbn",
                    "PageCount",
                ]),

            ["EBookModel"] = PortableEntity(
                portable:
                [
                    "Isbn",
                    "PageCount",
                ]),

            ["AudioBookModel"] = PortableEntity(
                portable:
                [
                    "Asin",
                    "Duration",
                    "Narrator",
                ]),

            ["BookMetadata"] = PortableEntity(
                portable:
                [
                    "Subtitle",
                    "Description",
                    "Editor",
                    "Translator",
                    "Publisher",
                    "PlaceOfPublication",
                    "PublishedDate",
                    "Language",
                    "Categories",
                    "Edition",
                    "Series",
                    "VolumeNumber",
                ],
                excluded:
                [
                    Excluded(
                        "BookModelId",
                        "EF Core owned entity shadow foreign key back to the owning book row; persistence detail."),
                ]),

            ["ReadingProgress"] = PortableEntity(
                portable:
                [
                    "LastLocation",
                    "ProgressPercent",
                    "Rating",
                    "IsFavorite",
                    "PersonalReview",
                    "LastReadAt",
                    "FinishedAt",
                ],
                excluded:
                [
                    Excluded(
                        "BookModelId",
                        "EF Core owned entity shadow foreign key back to the owning book row; persistence detail."),
                ]),

            ["FileInfoDetails"] = PortableEntity(
                portable:
                [
                    "HasFile",
                    "FileName",
                    "CoverFileName",
                    "ChaptersJson",
                ],
                excluded:
                [
                    Excluded(
                        "BookModelId",
                        "EF Core owned entity shadow foreign key back to the owning book row; persistence detail."),
                    Excluded(
                        "LocationsJson",
                        "Reconstructible client-side epub.js CFI locations cache; it is regenerated from the book."),
                ]),

            ["WorkModel"] = PortableEntity(
                portable:
                [
                    "Id",
                    "Title",
                    "Author",
                    "CreatedAt",
                ],
                excluded:
                [
                    Excluded(
                        "NormalizedTitle",
                        "Derived search optimization index column; recomputed from Title on import/save."),
                    Excluded(
                        "NormalizedAuthor",
                        "Derived search optimization index column; recomputed from Author on import/save."),
                ],
                excludedNavigations:
                [
                    Excluded("Books", NavigationReason),
                ]),

            ["CollectionModel"] = PortableEntity(
                portable:
                [
                    "Id",
                    "Name",
                    "ParentId",
                ],
                excludedNavigations:
                [
                    Excluded("Parent", NavigationReason),
                    Excluded("Children", NavigationReason),
                ]),

            ["BookCollectionModel"] = PortableEntity(
                portable:
                [
                    "BookId",
                    "CollectionId",
                    "AddedAt",
                ],
                excludedNavigations:
                [
                    Excluded("Book", NavigationReason),
                    Excluded("Collection", NavigationReason),
                ]),

            ["NoteModel"] = PortableEntity(
                portable:
                [
                    "Id",
                    "BookId",
                    "Content",
                    "CfiRange",
                    "SelectedText",
                    "CreatedAt",
                    "RawContent",
                    "CaptureSource",
                    "ProcessingMode",
                    "SourceAnchorKind",
                    "SourceAnchorValue",
                    "AnchorVerified",
                ],
                excludedNavigations:
                [
                    Excluded("Book", NavigationReason),
                    Excluded("NoteTopics", NavigationReason),
                ]),

            ["TopicModel"] = PortableEntity(
                portable:
                [
                    "Id",
                    "Topic",
                ],
                excludedNavigations:
                [
                    Excluded("NoteTopics", NavigationReason),
                ]),

            ["NoteTopicModel"] = PortableEntity(
                portable:
                [
                    "NoteId",
                    "TopicId",
                ],
                excludedNavigations:
                [
                    Excluded("Note", NavigationReason),
                    Excluded("Topic", NavigationReason),
                ]),

            ["WritingModel"] = PortableEntity(
                portable:
                [
                    "Id",
                    "Name",
                    "Type",
                    "Content",
                    "ParentId",
                    "CreatedAt",
                    "UpdatedAt",
                ],
                excludedNavigations:
                [
                    Excluded("Parent", NavigationReason),
                    Excluded("Children", NavigationReason),
                ]),

            ["WritingNoteModel"] = PortableEntity(
                portable:
                [
                    "WritingId",
                    "NoteId",
                    "AddedAt",
                ],
                excludedNavigations:
                [
                    Excluded("Writing", NavigationReason),
                    Excluded("Note", NavigationReason),
                ]),

            ["BookAcquisitionModel"] = PortableEntity(
                portable:
                [
                    "Id",
                    "BookId",
                    "ProviderId",
                    "ProviderDisplayName",
                    "ExternalId",
                    "AssetId",
                    "AssetFormat",
                    "ImportedExtension",
                    "SourceUrl",
                    "RightsStatement",
                    "AcquiredAt",
                ],
                excludedNavigations:
                [
                    Excluded("Book", NavigationReason),
                ]),

            ["AssistantSettingsModel"] = PortableEntity(
                portable:
                [
                    "CaptureProcessingMode",
                    "UpdatedAtUtc",
                ],
                excluded:
                [
                    Excluded(
                        "Id",
                        "Singleton primary-key identity is local persistence plumbing rather than portable library state."),
                ]),

            ["NoteImportBookLink"] = PortableEntity(
                portable:
                [
                    "Id",
                    "Source",
                    "SourceKey",
                    "BookId",
                    "CreatedAtUtc",
                ],
                excludedNavigations:
                [
                    Excluded("Book", NavigationReason),
                ]),

            ["NoteImportBatch"] = ExcludedEntity(
                "Import Undo history/local batch undo receipt. Once imported, notes are durable library notes; batch tracking is local operational history."),

            ["NoteImportBatchNote"] = ExcludedEntity(
                "Import Undo history/local batch undo receipt. Once imported, notes are durable library notes; batch tracking is local operational history."),

            ["AiProviderSettingsModel"] = ExcludedEntity(
                "Host credentials, API keys, and host AI-provider configuration must never be transferred as portable library data."),

            ["BackupRecord"] = ExcludedEntity(
                "Host-local operational backup record containing deployment-specific backup state and local file paths."),

            ["LibraryCommandReceipt"] = ExcludedEntity(
                "Operational idempotency receipt/command-deduplication log; not durable user library content."),

            ["NoteCommandReceipt"] = ExcludedEntity(
                "Operational idempotency receipt/command-deduplication log; not durable user library content."),

            ["LibraryState"] = ExcludedEntity(
                "Operational local event-store/synchronization state; not portable user library content."),

            ["MigrationJobRecord"] = ExcludedEntity(
                "Host-local operational migration state: job lifecycle, worker leases, progress, idempotency and failure/expiry bookkeeping. " +
                "This is local operational control state, not user library content, and must never be serialized into portable archives."),

            ["MigrationSessionRecord"] = ExcludedEntity(
                "Host-local operational migration transfer state: resumable upload session, relative staging storage reference, and chunk/byte accounting. " +
                "This is local operational transfer state, not user library content, and must never be serialized into portable archives."),

            ["MigrationChunkReceiptRecord"] = ExcludedEntity(
                "Host-local operational migration transfer receipts: accepted chunk offsets, lengths and hashes used to resume uploads. " +
                "These are local operational transfer receipts, not user library content, and must never be serialized into portable archives."),

            ["MigrationExportArtifactRecord"] = ExcludedEntity(
                "Host-local operational migration storage and retention metadata: generated export archive file references, retention deadlines and deletion timestamps. " +
                "This is local operational storage/retention state, not user library content, and must never be serialized into portable archives."),

            ["MigrationStorageReservationRecord"] = ExcludedEntity(
                "Host-local operational migration storage admission/accounting state: preflight reservations of temporary storage bytes with claim/release timestamps. " +
                "This is local operational capacity-accounting state, not user library content, and must never be serialized into portable archives."),
        };

    internal static IReadOnlySet<string> ExcludedEntityNames =>
        Inventory
            .Where(entry => entry.Value.IsEntityExcluded)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_mapped_entity_property_and_navigation_has_a_portability_classification()
    {
        using var db = CreateDbContext();

        var modelEntities = db.Model
            .GetEntityTypes()
            .OrderBy(EntityDisplayName, StringComparer.Ordinal)
            .ToArray();

        var failures = new List<string>();

        var modelEntityNames = modelEntities
            .Select(EntityInventoryName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entityType in modelEntities)
        {
            var entityName = EntityInventoryName(entityType);

            if (!Inventory.TryGetValue(entityName, out var classification))
            {
                failures.Add(
                    $"UNCLASSIFIED ENTITY: {EntityDisplayName(entityType)}. " +
                    "Add it to PortableCompletenessInventoryTests.Inventory as Portable or ExplicitlyExcluded.");

                continue;
            }

            ValidateEntity(entityType, classification, failures);
        }

        foreach (var staleInventoryEntry in Inventory.Keys
                     .Where(name => !modelEntityNames.Contains(name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE INVENTORY ENTITY: {staleInventoryEntry} is classified in the portability inventory " +
                "but is not present in NostosDbContext's EF model. Remove or rename the inventory entry deliberately.");
        }

        failures.Should().BeEmpty(
            "the migration completeness inventory must fail closed: every EF entity, mapped property, " +
            "navigation, and skip navigation must have an explicit Portable or ExplicitlyExcluded decision.{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, failures.Select(x => $" - {x}")));
    }

    private static readonly Type[] PortableArchivePayloadRootTypes =
    [
        typeof(PortableLibraryData),
        typeof(PortableArchiveManifest),
    ];

    private static readonly IReadOnlyDictionary<Type, ArchiveRecordClassification>
        PortableArchiveRecordInventory =
            new Dictionary<Type, ArchiveRecordClassification>
            {
                [typeof(PortableLibraryData)] = StructuralRecord(
                    ("Version", "Archive data compatibility version for the complete payload."),
                    ("Works", "Top-level archive collection containing PortableWork records."),
                    ("Books", "Top-level archive collection containing PortableBook records."),
                    ("Collections", "Top-level archive collection containing PortableCollection records."),
                    ("BookCollections", "Top-level archive collection containing PortableBookCollection records."),
                    ("Notes", "Top-level archive collection containing PortableNote records."),
                    ("Topics", "Top-level archive collection containing PortableTopic records under the legacy concepts key."),
                    ("NoteTopics", "Top-level archive collection containing PortableNoteTopic records under the legacy noteConcepts key."),
                    ("Writings", "Top-level archive collection containing PortableWriting records."),
                    ("BookAcquisitions", "Top-level archive collection containing PortableBookAcquisition records."),
                    ("AssistantSettings", "Optional top-level singleton containing PortableAssistantSettings."),
                    ("WritingNotes", "Version 2 optional top-level collection containing PortableWritingNote records."),
                    ("NoteImportBookLinks", "Version 3 optional top-level collection containing PortableNoteImportBookLink records.")),

                [typeof(PortableArchiveManifest)] = StructuralRecord(
                    ("Format", "Archive format identifier in the manifest JSON root."),
                    ("FormatVersion", "Archive structure version in the manifest JSON root."),
                    ("DataVersion", "Relational payload compatibility version in the manifest JSON root."),
                    ("ExportedAtUtc", "Export timestamp metadata."),
                    ("ApplicationVersion", "Exporter product version metadata."),
                    ("Counts", "Nested manifest summary of relational archive record counts."),
                    ("Data", "Nested manifest descriptor for the relational JSON payload."),
                    ("Media", "Nested manifest descriptors for media archive entries.")),

                [typeof(PortableArchiveCounts)] = StructuralRecord(
                    ("Works", "Count derived from PortableLibraryData.Works."),
                    ("Books", "Count derived from PortableLibraryData.Books."),
                    ("Collections", "Count derived from PortableLibraryData.Collections."),
                    ("BookCollections", "Count derived from PortableLibraryData.BookCollections."),
                    ("Notes", "Count derived from PortableLibraryData.Notes."),
                    ("Topics", "Count derived from PortableLibraryData.Topics."),
                    ("NoteTopics", "Count derived from PortableLibraryData.NoteTopics."),
                    ("Writings", "Count derived from PortableLibraryData.Writings."),
                    ("BookAcquisitions", "Count derived from PortableLibraryData.BookAcquisitions.")),

                [typeof(PortableArchivePayload)] = StructuralRecord(
                    ("Path", "Canonical archive path of the relational JSON payload."),
                    ("Length", "Declared relational payload length verified during import."),
                    ("Sha256", "SHA-256 digest verified against the relational payload bytes.")),

                [typeof(PortableArchiveMediaEntry)] = StructuralRecord(
                    ("BookId", "Stable book ID identifying the owning media asset."),
                    ("Kind", "Archive media role, either book file or cover."),
                    ("Path", "Canonical archive path derived from the stable book ID and media kind."),
                    ("FileName", "Canonical media filename preserving its supported extension, not a host path."),
                    ("ContentType", "Media content type metadata."),
                    ("Length", "Declared media length verified during import."),
                    ("Sha256", "SHA-256 digest verified against the staged media bytes.")),

                [typeof(PortableWork)] = EntityRecord(
                    ["WorkModel"],
                    incomingCountKind: "Works",
                    destinationCountKind: "Works"),

                [typeof(PortableBook)] = EntityRecord(
                    ["BookModel", "PhysicalBookModel", "EBookModel", "AudioBookModel", "FileInfoDetails"],
                    incomingCountKind: "Books",
                    destinationCountKind: "Books",
                    structuralProperties: StructuralFields(
                        ("Type", "TPH discriminator serialized as explicit book type string."),
                        ("Metadata", "Owned entity BookMetadata serialized as a nested PortableBookMetadata record."),
                        ("Progress", "Owned entity ReadingProgress serialized as a nested PortableReadingProgress record."),
                        ("HasBookFile", "Derived boolean indicating whether physical/ebook asset exists."),
                        ("HasCover", "Derived boolean indicating whether book cover asset exists."),
                        ("Isbn", "TPH subclass physical/ebook ISBN property serialized on the book record."),
                        ("PageCount", "TPH subclass physical/ebook page count serialized on the book record."),
                        ("Asin", "TPH subclass audiobook ASIN serialized on the book record."),
                        ("Duration", "TPH subclass audiobook duration serialized on the book record."),
                        ("Narrator", "TPH subclass audiobook narrator serialized on the book record.")),
                    entityPropertyAliases: EntityPropertyAliases(
                        ("FileInfoDetails", "HasFile", typeof(PortableBook), "HasBookFile",
                            "Asset-presence flag is serialized as PortableBook.HasBookFile."),
                        ("FileInfoDetails", "FileName", typeof(PortableArchiveMediaEntry), "FileName",
                            "Host-local book filename is replaced by the canonical archive media filename."),
                        ("FileInfoDetails", "CoverFileName", typeof(PortableArchiveMediaEntry), "FileName",
                            "Host-local cover filename is replaced by the canonical archive media filename."))),

                [typeof(PortableBookMetadata)] = EntityRecord(
                    ["BookMetadata"],
                    incomingCountKind: "Books",
                    destinationCountKind: "Books"),

                [typeof(PortableReadingProgress)] = EntityRecord(
                    ["ReadingProgress"],
                    incomingCountKind: "Books",
                    destinationCountKind: "Books"),

                [typeof(PortableCollection)] = EntityRecord(
                    ["CollectionModel"],
                    incomingCountKind: "Collections",
                    destinationCountKind: "Collections"),

                [typeof(PortableBookCollection)] = EntityRecord(
                    ["BookCollectionModel"],
                    incomingCountKind: "CollectionMemberships",
                    destinationCountKind: "BookCollections"),

                [typeof(PortableNote)] = EntityRecord(
                    ["NoteModel"],
                    incomingCountKind: "Notes",
                    destinationCountKind: "Notes"),

                [typeof(PortableTopic)] = EntityRecord(
                    ["TopicModel"],
                    incomingCountKind: "Topics",
                    destinationCountKind: "Topics"),

                [typeof(PortableNoteTopic)] = EntityRecord(
                    ["NoteTopicModel"],
                    incomingCountKind: "NoteTopics",
                    destinationCountKind: "NoteTopics"),

                [typeof(PortableWriting)] = EntityRecord(
                    ["WritingModel"],
                    incomingCountKind: "Writings",
                    destinationCountKind: "Writings"),

                [typeof(PortableWritingNote)] = EntityRecord(
                    ["WritingNoteModel"],
                    incomingCountKind: "WritingNotes",
                    destinationCountKind: "WritingNotes"),

                [typeof(PortableBookAcquisition)] = EntityRecord(
                    ["BookAcquisitionModel"],
                    incomingCountKind: "Acquisitions",
                    destinationCountKind: "Acquisitions"),

                [typeof(PortableAssistantSettings)] = EntityRecord(
                    ["AssistantSettingsModel"],
                    incomingCountKind: "AssistantSettings",
                    destinationCountKind: "AssistantSettings"),

                [typeof(PortableNoteImportBookLink)] = EntityRecord(
                    ["NoteImportBookLink"],
                    incomingCountKind: "NoteImportBookLinks",
                    destinationCountKind: "NoteImportBookLinks"),
            };

    private static readonly IReadOnlyDictionary<string, string> AdditionalIncomingCountKinds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(MigrationArchiveCounts.MediaEntries)] =
                "Count of media entries is derived from the portable media manifest rather than an EF entity kind.",
        };

    internal static IReadOnlySet<string> ExpectedIncomingCountProperties =>
        GetCountProperties(classification => classification.IncomingCountKind)
            .Concat(AdditionalIncomingCountKinds.Keys)
            .Append(nameof(MigrationArchiveCounts.TotalRows))
            .ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlySet<string> ExpectedDestinationCountProperties =>
        GetCountProperties(classification => classification.DestinationCountKind)
            .Append(nameof(MigrationExistingCounts.TotalRows))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Portable EF entity properties by entity name. Verifier candidate coverage is
    /// asserted against this projection so a classified portable property that no
    /// executable comparison checks fails the verifier coverage tests.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> PortablePropertiesByEntity =>
        Inventory
            .Where(pair => !pair.Value.IsEntityExcluded)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.PortableProperties
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

    [Fact]
    public void Portable_classification_and_archive_records_have_two_way_property_parity()
    {
        var discoveredRecordTypes = DiscoverPortableArchiveRecordTypes();
        var discoveredRecordProperties = discoveredRecordTypes.ToDictionary(
            recordType => recordType,
            recordType => recordType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod is not null)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal));
        var failures = new List<string>();

        foreach (var unclassifiedRecord in discoveredRecordTypes
                     .Where(recordType => !PortableArchiveRecordInventory.ContainsKey(recordType))
                     .OrderBy(recordType => recordType.Name, StringComparer.Ordinal))
        {
            failures.Add(
                $"UNCLASSIFIED ARCHIVE RECORD: {unclassifiedRecord.Name}. " +
                "Register it as mapped to classified entity properties or explicitly structural with reasons.");
        }

        foreach (var staleRecord in PortableArchiveRecordInventory.Keys
                     .Where(recordType => !discoveredRecordTypes.Contains(recordType))
                     .OrderBy(recordType => recordType.Name, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE ARCHIVE RECORD CLASSIFICATION: {staleRecord.Name} is registered but is not reachable " +
                "from a portable archive payload root.");
        }

        foreach (var archiveRecordType in discoveredRecordTypes
                     .OrderBy(recordType => recordType.Name, StringComparer.Ordinal))
        {
            if (!PortableArchiveRecordInventory.TryGetValue(archiveRecordType, out var recordClassification))
                continue;

            if (recordClassification.EntityInventoryNames.Count > 0
                && (string.IsNullOrWhiteSpace(recordClassification.IncomingCountKind)
                    || string.IsNullOrWhiteSpace(recordClassification.DestinationCountKind)))
            {
                failures.Add(
                    $"MISSING PREFLIGHT COUNT CLASSIFICATION: {archiveRecordType.Name} maps to portable entity " +
                    "data and must provide incoming and destination count kinds.");
            }

            var archiveProperties = archiveRecordType
                .GetProperties(
                    BindingFlags.Public |
                    BindingFlags.Instance)
                .Where(property => property.GetMethod is not null)
                .ToArray();

            foreach (var entityName in recordClassification.EntityInventoryNames)
            {
                if (!Inventory.TryGetValue(entityName, out var entityClassification))
                {
                    failures.Add(
                        $"UNCLASSIFIED ARCHIVE ENTITY MAPPING: {archiveRecordType.Name} maps to missing " +
                        $"inventory entity {entityName}.");
                    continue;
                }

                if (entityClassification.IsEntityExcluded)
                {
                    failures.Add(
                        $"INVALID ARCHIVE ENTITY MAPPING: {archiveRecordType.Name} maps to explicitly excluded " +
                        $"entity {entityName}.");
                    continue;
                }

                foreach (var portableProperty in entityClassification.PortableProperties)
                {
                    if (!archiveProperties.Any(property => property.Name == portableProperty))
                    {
                        if (!recordClassification.EntityPropertyAliases.TryGetValue(
                                (entityName, portableProperty),
                                out var alias))
                        {
                            failures.Add(
                                $"MISSING ARCHIVE PROPERTY: {archiveRecordType.Name} does not carry classified " +
                                $"portable entity property {entityName}.{portableProperty}.");
                        }
                        else if (string.IsNullOrWhiteSpace(alias.Reason)
                                 || !discoveredRecordProperties.TryGetValue(
                                     alias.ArchiveRecordType,
                                     out var targetProperties)
                                 || !targetProperties.Contains(alias.ArchivePropertyName))
                        {
                            failures.Add(
                                $"INVALID ARCHIVE PROPERTY MAPPING: {entityName}.{portableProperty} maps to " +
                                $"{alias.ArchiveRecordType.Name}.{alias.ArchivePropertyName}, which is missing or has no reason.");
                        }
                    }
                }
            }

            foreach (var (entityProperty, alias) in recordClassification.EntityPropertyAliases)
            {
                if (!Inventory.TryGetValue(entityProperty.EntityName, out var aliasedEntity)
                    || !aliasedEntity.PortableProperties.Contains(entityProperty.PropertyName)
                    || string.IsNullOrWhiteSpace(alias.Reason)
                    || !discoveredRecordProperties.TryGetValue(alias.ArchiveRecordType, out var targetProperties)
                    || !targetProperties.Contains(alias.ArchivePropertyName))
                {
                    failures.Add(
                        $"STALE ARCHIVE PROPERTY MAPPING: {entityProperty.EntityName}.{entityProperty.PropertyName} " +
                        $"maps to {alias.ArchiveRecordType.Name}.{alias.ArchivePropertyName} but the entity field or target is missing.");
                }
            }

            foreach (var archiveProperty in archiveProperties)
            {
                if (recordClassification.StructuralProperties.TryGetValue(
                        archiveProperty.Name,
                        out var structuralReason))
                {
                    if (string.IsNullOrWhiteSpace(structuralReason))
                    {
                        failures.Add(
                            $"MISSING STRUCTURAL ARCHIVE REASON: {archiveRecordType.Name}.{archiveProperty.Name} " +
                            "must provide an architectural rationale.");
                    }

                    continue;
                }

                var mappedToPortableEntityProperty = recordClassification.EntityInventoryNames
                    .Where(Inventory.ContainsKey)
                    .Select(entityName => Inventory[entityName])
                    .Where(entityClassification => !entityClassification.IsEntityExcluded)
                    .Any(entityClassification =>
                        entityClassification.PortableProperties.Contains(archiveProperty.Name));

                if (!mappedToPortableEntityProperty)
                {
                    failures.Add(
                        $"UNACCOUNTED ARCHIVE PROPERTY: {archiveRecordType.Name}.{archiveProperty.Name}. " +
                        "Map it to a classified portable entity property or register it as derived/structural with a reason.");
                }
            }

            foreach (var (structuralField, reason) in recordClassification.StructuralProperties)
            {
                reason.Should().NotBeNullOrWhiteSpace(
                    $"{archiveRecordType.Name}.{structuralField} requires a documented reason");

                if (!archiveProperties.Any(property => property.Name == structuralField))
                {
                    failures.Add(
                        $"STALE STRUCTURAL ARCHIVE FIELD: {archiveRecordType.Name}.{structuralField} " +
                        "is registered but no such public instance property exists.");
                }
            }
        }

        failures.Should().BeEmpty(
            "every archive record and property reachable from a payload root must be accounted for by the " +
            "portable completeness inventory or explicitly classified as derived/structural.{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, failures.Select(failure => $" - {failure}")));
    }

    private static IReadOnlySet<Type> DiscoverPortableArchiveRecordTypes()
    {
        var discovered = new HashSet<Type>();
        var pending = new Queue<Type>(PortableArchivePayloadRootTypes);

        while (pending.TryDequeue(out var candidate))
        {
            candidate = Nullable.GetUnderlyingType(candidate) ?? candidate;

            if (candidate == typeof(string))
                continue;

            if (candidate.IsArray)
            {
                pending.Enqueue(candidate.GetElementType()!);
                continue;
            }

            var enumerableType = candidate
                .GetInterfaces()
                .Append(candidate)
                .FirstOrDefault(type =>
                    type.IsGenericType &&
                    type.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            if (enumerableType is not null)
            {
                pending.Enqueue(enumerableType.GetGenericArguments()[0]);
                continue;
            }

            if (!IsPortableArchiveRecordType(candidate))
            {
                if (candidate.IsGenericType)
                {
                    foreach (var genericArgument in candidate.GetGenericArguments())
                        pending.Enqueue(genericArgument);
                }

                continue;
            }

            if (!discovered.Add(candidate))
                continue;

            foreach (var property in candidate.GetProperties(
                         BindingFlags.Public |
                         BindingFlags.Instance))
            {
                pending.Enqueue(property.PropertyType);
            }
        }

        return discovered;
    }

    private static bool IsPortableArchiveRecordType(Type candidate) =>
        candidate.IsClass &&
        candidate != typeof(string) &&
        candidate.Assembly == typeof(PortableLibraryData).Assembly &&
        candidate.Namespace == typeof(PortableLibraryData).Namespace;

    private static IReadOnlySet<string> GetCountProperties(
        Func<ArchiveRecordClassification, string?> selectCountKind) =>
        PortableArchiveRecordInventory.Values
            .Select(selectCountKind)
            .Where(countKind => !string.IsNullOrWhiteSpace(countKind))
            .Select(countKind => countKind!)
            .ToHashSet(StringComparer.Ordinal);

    private static ArchiveRecordClassification EntityRecord(
        IEnumerable<string> entityInventoryNames,
        string incomingCountKind,
        string destinationCountKind,
        IReadOnlyDictionary<string, string>? structuralProperties = null,
        IReadOnlyDictionary<(string EntityName, string PropertyName), ArchivePropertyAlias>? entityPropertyAliases = null) =>
        new(
            entityInventoryNames.ToArray(),
            structuralProperties ?? new Dictionary<string, string>(StringComparer.Ordinal),
            entityPropertyAliases ?? new Dictionary<(string EntityName, string PropertyName), ArchivePropertyAlias>(),
            incomingCountKind,
            destinationCountKind);

    private static ArchiveRecordClassification StructuralRecord(
        params (string Property, string Reason)[] properties) =>
        new(
            [],
            StructuralFields(properties),
            new Dictionary<(string EntityName, string PropertyName), ArchivePropertyAlias>(),
            IncomingCountKind: null,
            DestinationCountKind: null);

    private static IReadOnlyDictionary<string, string> StructuralFields(
        params (string Property, string Reason)[] properties) =>
        properties.ToDictionary(
            property => property.Property,
            property => property.Reason,
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<(string EntityName, string PropertyName), ArchivePropertyAlias>
        EntityPropertyAliases(
            params (string EntityName, string PropertyName, Type ArchiveRecordType, string ArchivePropertyName, string Reason)[] aliases) =>
        aliases.ToDictionary(
            alias => (alias.EntityName, alias.PropertyName),
            alias => new ArchivePropertyAlias(
                alias.ArchiveRecordType,
                alias.ArchivePropertyName,
                alias.Reason));

    private static void ValidateEntity(
        IEntityType entityType,
        EntityClassification classification,
        List<string> failures)
    {
        var entityName = EntityDisplayName(entityType);

        if (classification.IsEntityExcluded)
        {
            classification.ExclusionReason.Should().NotBeNullOrWhiteSpace(
                $"excluded entity {entityName} must provide an explicit exclusion rationale.");

            return;
        }

        var properties = entityType
            .GetProperties()
            .Where(p => p.DeclaringType == entityType)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        var mappedPropertyNames = properties
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var property in properties)
        {
            var propName = property.Name;
            var isPortable = classification.PortableProperties.Contains(propName);
            var isExcluded = classification.ExcludedProperties.TryGetValue(propName, out var reason);

            if (!isPortable && !isExcluded)
            {
                failures.Add(
                    $"UNCLASSIFIED PROPERTY: {entityName}.{propName} (Type: {property.ClrType.Name}). " +
                    "Classify as portable or explicitly excluded with rationale.");
            }
            else if (isPortable && isExcluded)
            {
                failures.Add(
                    $"CONFLICTING CLASSIFICATION: {entityName}.{propName} is listed as both portable and excluded.");
            }
            else if (isExcluded && string.IsNullOrWhiteSpace(reason))
            {
                failures.Add(
                    $"MISSING EXCLUSION REASON: {entityName}.{propName} must provide an explicit exclusion rationale.");
            }
        }

        foreach (var staleProp in classification.PortableProperties
                     .Where(name => !mappedPropertyNames.Contains(name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE PORTABLE PROPERTY: {entityName}.{staleProp} does not exist on the EF model. " +
                "Update the inventory after property removals/renames.");
        }

        foreach (var staleExcluded in classification.ExcludedProperties.Keys
                     .Where(name => !mappedPropertyNames.Contains(name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE EXCLUDED PROPERTY: {entityName}.{staleExcluded} does not exist on the EF model. " +
                "Update the inventory after property removals/renames.");
        }

        var navigations = entityType
            .GetNavigations()
            .Where(n => n.DeclaringType == entityType)
            .Select(n => n.Name)
            .Concat(
                entityType
                    .GetSkipNavigations()
                    .Where(n => n.DeclaringType == entityType)
                    .Select(n => n.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var mappedNavigationNames = navigations
            .ToHashSet(StringComparer.Ordinal);

        foreach (var navigationName in navigations)
        {
            if (!classification.ExcludedNavigations.TryGetValue(navigationName, out var reason))
            {
                failures.Add(
                    $"UNCLASSIFIED NAVIGATION: {entityName}.{navigationName}. " +
                    "Navigations must be explicitly excluded with an architectural reason.");
            }
            else if (string.IsNullOrWhiteSpace(reason))
            {
                failures.Add(
                    $"MISSING NAVIGATION EXCLUSION REASON: {entityName}.{navigationName} must provide an explicit rationale.");
            }
        }

        foreach (var staleNavigation in classification.ExcludedNavigations.Keys
                     .Where(name => !mappedNavigationNames.Contains(name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE EXCLUDED NAVIGATION: {entityName}.{staleNavigation} does not exist on the EF model. " +
                "Update the inventory after navigation removals/renames.");
        }
    }

    private static NostosDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        return new NostosDbContext(options);
    }

    private static string EntityInventoryName(IEntityType entityType) =>
        entityType.ClrType.Name;

    private static string EntityDisplayName(IEntityType entityType) =>
        entityType.ClrType?.Name ?? entityType.Name;

    private static KeyValuePair<string, string> Excluded(string memberName, string reason) =>
        new(memberName, reason);

    private static EntityClassification PortableEntity(
        IEnumerable<string>? portable = null,
        IEnumerable<KeyValuePair<string, string>>? excluded = null,
        IEnumerable<KeyValuePair<string, string>>? excludedNavigations = null,
        bool portableAllMappedProperties = false) =>
        new(
            IsEntityExcluded: false,
            ExclusionReason: null,
            PortableProperties:
                portable?.ToHashSet(StringComparer.Ordinal) ??
                new HashSet<string>(StringComparer.Ordinal),
            ExcludedProperties:
                excluded?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) ??
                new Dictionary<string, string>(StringComparer.Ordinal),
            ExcludedNavigations:
                excludedNavigations?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) ??
                new Dictionary<string, string>(StringComparer.Ordinal));

    private static EntityClassification ExcludedEntity(string reason) =>
        new(
            IsEntityExcluded: true,
            ExclusionReason: reason,
            PortableProperties: new HashSet<string>(StringComparer.Ordinal),
            ExcludedProperties: new Dictionary<string, string>(StringComparer.Ordinal),
            ExcludedNavigations: new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed record ArchiveRecordClassification(
        IReadOnlyList<string> EntityInventoryNames,
        IReadOnlyDictionary<string, string> StructuralProperties,
        IReadOnlyDictionary<(string EntityName, string PropertyName), ArchivePropertyAlias> EntityPropertyAliases,
        string? IncomingCountKind,
        string? DestinationCountKind);

    private sealed record ArchivePropertyAlias(
        Type ArchiveRecordType,
        string ArchivePropertyName,
        string Reason);

    private sealed record EntityClassification(
        bool IsEntityExcluded,
        string? ExclusionReason,
        HashSet<string> PortableProperties,
        Dictionary<string, string> ExcludedProperties,
        Dictionary<string, string> ExcludedNavigations);
}
