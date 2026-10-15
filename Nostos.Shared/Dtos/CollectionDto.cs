using System;
using System.Text.Json.Serialization;

namespace Nostos.Shared.Dtos;

public record CollectionDto(
    Guid Id,
    string Name,
    Guid? ParentId
);

public record CreateCollectionDto(
    string Name,
    Guid? ParentId
);

// Full-replacement PUT contract (collections Phase 1): both fields are
// REQUIRED on the wire — a missing name or missing parentId is a 400.
// Explicit JSON null parentId means "move to root" and is distinct from a
// missing property because [JsonRequired] enforces property presence.
public sealed record UpdateCollectionDto(
    [property: JsonRequired] string Name,
    [property: JsonRequired] Guid? ParentId
);

// REST-only sidebar counts: descendant-inclusive book counts per collection.
// Deliberately NOT added to CollectionDto, so the plain list stays small.
public sealed record CollectionCountDto(
    Guid CollectionId,
    int BookCount
);
