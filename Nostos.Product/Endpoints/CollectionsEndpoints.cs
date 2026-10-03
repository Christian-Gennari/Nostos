using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

public static class CollectionsEndpoints
{
    public static IEndpointRouteBuilder MapCollectionsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/collections");

        // GET: Fetch all collections (FLAT)
        group.MapGet(
            "/",
            async (ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.ListCollectionsAsync(ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // GET: descendant-inclusive book counts for every collection
        // (sidebar; REST-only — CollectionDto stays frozen for MCP).
        // Registered before /{id} so the literal segment always wins.
        group.MapGet(
            "/counts",
            async (ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.ListCollectionCountsAsync(ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // GET one collection
        group.MapGet(
            "/{id}",
            async (Guid id, ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.GetCollectionAsync(id, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // CREATE collection (duplicate sibling name returns the existing
        // collection, mirroring the canonical service contract)
        group.MapPost(
            "/",
            async (CreateCollectionDto dto, ILibraryService library, CancellationToken ct) =>
            {
                var request = new LibraryCreateCollectionRequest(
                    "rest", $"rest-collection-create-{Guid.NewGuid():N}",
                    dto.Name, dto.ParentId);

                var result = await library.CreateCollectionAsync(request, ct);
                if (LibraryHttpMapper.MapError(result) is { } error)
                    return error;

                var collection = (CollectionDto)result.Data!;
                return Results.Created($"/api/collections/{collection.Id}", collection);
            }
        );

        // UPDATE collection (atomic full replacement, collections Phase 1):
        // the canonical service performs sibling-collision and cycle
        // detection and applies rename+move in ONE transaction, with one
        // receipt and at most one stateVersion bump. Missing name OR missing
        // parentId is a 400 ([JsonRequired]); explicit null parentId moves
        // the collection to root.
        group.MapPut(
            "/{id}",
            async (Guid id, UpdateCollectionDto dto, ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.UpdateCollectionAsync(
                    clientId: "rest",
                    idempotencyKey: $"rest-collection-update-{Guid.NewGuid():N}",
                    collectionId: id,
                    name: dto.Name,
                    parentId: dto.ParentId,
                    ct);

                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // DELETE collection (books are unlinked, never deleted; children must
        // be moved/deleted first)
        group.MapDelete(
            "/{id}",
            async (Guid id, ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.DeleteCollectionAsync(new LibraryDeleteCollectionRequest(
                    "rest", $"rest-collection-delete-{Guid.NewGuid():N}", id, Confirm: true), ct);

                return LibraryHttpMapper.MapError(result) ?? Results.NoContent();
            }
        );

        return routes;
    }
}
