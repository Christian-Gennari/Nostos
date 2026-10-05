using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data;

/// <summary>
/// The single shared source of truth for which EF entity types carry portable
/// user-owned library state (issue #679 Slice 11).
///
/// <para>Product code uses this set to decide whether a save advances the
/// destination revision (<see cref="NostosDbContext"/>). The portability
/// completeness inventory test asserts exact parity between this set and its
/// own Portable/ExplicitlyExcluded classification, so adding a portable entity
/// to the model without classifying it here (or vice versa) fails the suite.
/// The set lists every CLR type of a portable EF entity, including TPH
/// subclasses and owned types; only the CLR type identity matters, because
/// <c>ChangeTracker.Entries()</c> reports the concrete entry type.</para>
/// </summary>
public static class PortableEntitySet
{
    /// <summary>
    /// Every entity type whose rows are serialized into a portable archive.
    /// Keep this list in exact parity with
    /// <c>PortableCompletenessInventoryTests.Inventory</c>.
    /// </summary>
    public static readonly IReadOnlySet<Type> EntityTypes = new HashSet<Type>
    {
        typeof(BookModel),
        typeof(PhysicalBookModel),
        typeof(EBookModel),
        typeof(AudioBookModel),
        typeof(BookMetadata),
        typeof(ReadingProgress),
        typeof(FileInfoDetails),
        typeof(WorkModel),
        typeof(CollectionModel),
        typeof(BookCollectionModel),
        typeof(NoteModel),
        typeof(TopicModel),
        typeof(NoteTopicModel),
        typeof(WritingModel),
        typeof(WritingNoteModel),
        typeof(BookAcquisitionModel),
        typeof(AssistantSettingsModel),
        typeof(NoteImportBookLink),
    };

    /// <summary>Inventory-style names for parity assertions (one per entity type).</summary>
    public static readonly IReadOnlySet<string> EntityTypeNames =
        EntityTypes.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>True when a change-tracker entry of <paramref name="entityType"/> is portable state.</summary>
    public static bool IsPortableType(Type entityType) => EntityTypes.Contains(entityType);
}
