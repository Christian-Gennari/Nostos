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
        };

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

    [Fact]
    public void Portable_classifications_match_portable_archive_record_properties()
    {
        var portableRecordByEntity = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["WorkModel"] = typeof(PortableWork),
            ["BookModel"] = typeof(PortableBook),
            ["CollectionModel"] = typeof(PortableCollection),
            ["BookCollectionModel"] = typeof(PortableBookCollection),
            ["NoteModel"] = typeof(PortableNote),
            ["TopicModel"] = typeof(PortableTopic),
            ["NoteTopicModel"] = typeof(PortableNoteTopic),
            ["WritingModel"] = typeof(PortableWriting),
            ["WritingNoteModel"] = typeof(PortableWritingNote),
            ["BookAcquisitionModel"] = typeof(PortableBookAcquisition),
            ["AssistantSettingsModel"] = typeof(PortableAssistantSettings),
            ["NoteImportBookLink"] = typeof(PortableNoteImportBookLink),
        };

        foreach (var (entityName, portableRecordType) in portableRecordByEntity)
        {
            Inventory.Should().ContainKey(
                entityName,
                $"{entityName} must have an explicit portability classification");

            var classifiedPortableProperties = Inventory[entityName]
                .PortableProperties
                .ToHashSet(StringComparer.Ordinal);

            var archiveProperties = portableRecordType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

            archiveProperties.Should().Contain(
                classifiedPortableProperties,
                $"{entityName} properties classified Portable must be carried by {portableRecordType.Name}");
        }
    }

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

    private sealed record EntityClassification(
        bool IsEntityExcluded,
        string? ExclusionReason,
        HashSet<string> PortableProperties,
        Dictionary<string, string> ExcludedProperties,
        Dictionary<string, string> ExcludedNavigations);
}
