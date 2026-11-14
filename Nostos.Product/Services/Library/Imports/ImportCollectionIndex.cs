using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// The collections one import run knows about (issue #1625). A tag or a shelf
/// becomes a collection at most once per run; one that is already here is reused
/// by its normalized name — at any depth, which is what these imports have always
/// done, and looser than the library's own create rule, which only compares
/// siblings at the destination. Creating goes through <see cref="ILibraryService"/>,
/// so the name rules and the duplicate check are the library's, not a copy.
/// </summary>
internal sealed class ImportCollectionIndex(IEnumerable<CollectionModel> collections)
{
    private readonly List<CollectionModel> _collections = [.. collections];

    /// <summary>The collection with this name, whatever its parent.</summary>
    public CollectionModel? Find(string name)
    {
        var normalized = BookIdentityNormalizer.NormalizeTitle(name);
        return _collections.FirstOrDefault(collection =>
            string.Equals(
                BookIdentityNormalizer.NormalizeTitle(collection.Name),
                normalized,
                StringComparison.Ordinal));
    }

    /// <summary>The collection with this id, when this run already knows it.</summary>
    public CollectionModel? FindById(Guid collectionId) =>
        _collections.FirstOrDefault(collection => collection.Id == collectionId);

    /// <summary>The collection for this name: the one already here, or a new one.</summary>
    public async Task<(CollectionModel Collection, bool Created)> EnsureAsync(
        string name,
        ILibraryService library,
        string clientId,
        CancellationToken ct)
    {
        if (Find(name) is { } existing)
            return (existing, false);

        var outcome = await library.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(clientId, $"{clientId}-{Guid.NewGuid():N}", name),
            ct);
        if (outcome.Data is not CollectionDto created)
            throw new InvalidOperationException($"The collection for \"{name}\" could not be created.");

        var collection = new CollectionModel
        {
            Id = created.Id,
            Name = created.Name,
            ParentId = created.ParentId,
        };
        _collections.Add(collection);
        return (collection, true);
    }
}
