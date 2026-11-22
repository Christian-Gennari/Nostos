using System.Text.Json;
using System.Text.Json.Nodes;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Library organisation: every capability here stages into the turn's
/// changeset and writes nothing itself. The change policy decides at the end of
/// the turn whether the whole changeset applies at once (with undo) or waits
/// for the user's review, from what it would actually do.
/// </summary>
public static partial class AssistantCapabilities
{
    private const string CollectionArgument =
        "An existing collection's id, or the name of a collection created earlier in this turn.";

    private const string TopicKindArgument =
        "An existing kind's id or name, or the name of a kind created earlier in this turn.";

    private static IReadOnlyList<AssistantCapability> BuildChangeCapabilities(ITopicRepository topics) =>
    [
        Stage(
            "library_create_collection",
            "Creates a collection. Later changes in this turn can name it by its name. Creating a collection that already exists at that level reuses it.",
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The new collection's name. Required." },
                "parent": { "type": "string", "description": "The parent collection: an existing collection's id, or the name of a collection created earlier in this turn. Omit for the top level." }
              },
              "required": ["name"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                var name = Str(args, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return Invalid("'name' is required.");

                var dependsOn = new List<int>();
                LibraryCollectionRef? parent = null;
                if ((Str(args, "parent") ?? Str(args, "parentId")) is { } parentArg)
                {
                    if (!draft.TryResolveCollection(parentArg, out var resolved, out var createdBy))
                        return UnknownCollection(parentArg);
                    parent = resolved;
                    if (createdBy is { } step) dependsOn.Add(step);
                }

                return await draft.StageAsync(new CreateCollectionOp(name, name, parent), dependsOn, ct);
            }),

        Stage(
            "library_rename_collection",
            "Renames a collection.",
            $$"""
            {
              "type": "object",
              "properties": {
                "collection": { "type": "string", "description": "{{CollectionArgument}} Required." },
                "name": { "type": "string", "description": "The new name. Required." }
              },
              "required": ["collection", "name"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                var name = Str(args, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return Invalid("'name' is required.");
                if (!TryCollection(draft, args, "collection", out var collection, out var dependsOn, out var failure))
                    return failure!;

                return await draft.StageAsync(new RenameCollectionOp(collection, name), dependsOn, ct);
            }),

        Stage(
            "library_move_collection",
            "Moves a collection under another collection, or to the top level.",
            $$"""
            {
              "type": "object",
              "properties": {
                "collection": { "type": "string", "description": "{{CollectionArgument}} Required." },
                "newParent": { "type": "string", "description": "The new parent: an existing collection's id, or the name of a collection created earlier in this turn. Omit to move it to the top level." }
              },
              "required": ["collection"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                if (!TryCollection(draft, args, "collection", out var collection, out var dependsOn, out var failure))
                    return failure!;

                LibraryCollectionRef? newParent = null;
                if ((Str(args, "newParent") ?? Str(args, "newParentId")) is { } parentArg)
                {
                    if (!draft.TryResolveCollection(parentArg, out var resolved, out var createdBy))
                        return UnknownCollection(parentArg);
                    newParent = resolved;
                    if (createdBy is { } step) dependsOn = [.. dependsOn, step];
                }

                return await draft.StageAsync(new MoveCollectionOp(collection, newParent), dependsOn, ct);
            }),

        Stage(
            "library_delete_collection",
            "Deletes a collection together with its subcollections. Books are never deleted; they only leave these collections. Do not delete an existing collection only to recreate one with the same name; reuse its existing id instead.",
            $$"""
            {
              "type": "object",
              "properties": {
                "collection": { "type": "string", "description": "{{CollectionArgument}} Required." }
              },
              "required": ["collection"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                if (!TryCollection(draft, args, "collection", out var collection, out var dependsOn, out var failure))
                    return failure!;

                return await draft.StageAsync(new DeleteCollectionOp(collection), dependsOn, ct);
            }),

        Stage(
            "library_add_books_to_collection",
            "Adds books to a collection. Each book keeps the other collections it is in.",
            $$"""
            {
              "type": "object",
              "properties": {
                "collection": { "type": "string", "description": "{{CollectionArgument}} Required." },
                "bookIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "description": "The books to add. Required." }
              },
              "required": ["collection", "bookIds"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                if (!TryCollection(draft, args, "collection", out var collection, out var dependsOn, out var failure))
                    return failure!;
                if (!TryIds(args, "bookIds", out var bookIds) || bookIds is not { Count: > 0 })
                    return Invalid("'bookIds' must be a non-empty array of book ids.");

                return await draft.StageAsync(new AddBooksToCollectionOp(collection, bookIds), dependsOn, ct);
            }),

        Stage(
            "library_remove_books_from_collection",
            "Takes books out of a collection. The books themselves stay in the library.",
            $$"""
            {
              "type": "object",
              "properties": {
                "collection": { "type": "string", "description": "{{CollectionArgument}} Required." },
                "bookIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "description": "The books to take out. Required." }
              },
              "required": ["collection", "bookIds"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                if (!TryCollection(draft, args, "collection", out var collection, out var dependsOn, out var failure))
                    return failure!;
                if (!TryIds(args, "bookIds", out var bookIds) || bookIds is not { Count: > 0 })
                    return Invalid("'bookIds' must be a non-empty array of book ids.");

                return await draft.StageAsync(new RemoveBooksFromCollectionOp(collection, bookIds), dependsOn, ct);
            }),

        Stage(
            "library_set_book_collections",
            "Sets exactly which collections each listed book is in, replacing its current memberships. Prefer add/remove unless every membership of the book is meant to change.",
            """
            {
              "type": "object",
              "properties": {
                "updates": {
                  "type": "array",
                  "description": "One entry per book: the book and the complete list of collections it should be in afterwards. An empty list takes it out of every collection.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "bookId": { "type": "string", "format": "uuid" },
                      "collections": { "type": "array", "items": { "type": "string" }, "description": "Existing collection ids, or names of collections created earlier in this turn." }
                    },
                    "required": ["bookId", "collections"]
                  }
                }
              },
              "required": ["updates"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                var updates = Property(args, "updates");
                if (updates.ValueKind != JsonValueKind.Array || updates.GetArrayLength() == 0)
                    return Invalid("'updates' must be a non-empty array.");

                var additions = new List<(LibraryChangeOp, IReadOnlyList<int>)>();
                var index = 0;
                foreach (var item in updates.EnumerateArray())
                {
                    var collections = Property(item, "collections");
                    if (collections.ValueKind is JsonValueKind.Undefined)
                        collections = Property(item, "collectionIds");
                    if (Id(item, "bookId") is not { } bookId || collections.ValueKind != JsonValueKind.Array)
                        return Invalid($"updates[{index}] needs a 'bookId' and a 'collections' array.");

                    var refs = new List<LibraryCollectionRef>();
                    var dependsOn = new List<int>();
                    foreach (var value in collections.EnumerateArray())
                    {
                        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        if (!draft.TryResolveCollection(text, out var resolved, out var createdBy))
                            return UnknownCollection(text);
                        refs.Add(resolved);
                        if (createdBy is { } step) dependsOn.Add(step);
                    }

                    additions.Add((new SetBookCollectionsOp(bookId, refs), dependsOn));
                    index++;
                }

                return await draft.StageAsync(additions, ct);
            }),

        Stage(
            "library_update_book",
            "Edits a book's own details. Only the fields given change. Rating, favourite and review belong to the book: they are the same for every edition of it, so one change covers them all. A single edition can carry its own rating on top (the narration of an audiobook, say): set 'editionRating' only when the user names the edition, with that edition's id as bookId.",
            """
            {
              "type": "object",
              "properties": {
                "bookId": { "type": "string", "format": "uuid", "description": "The book. Required. With 'editionRating', the id of the edition the user named." },
                "title": { "type": "string", "description": "New title." },
                "author": { "type": "string", "description": "New author." },
                "description": { "type": "string", "description": "New description; an empty string clears it." },
                "rating": { "type": "integer", "description": "The book's rating from 0 to 5, shared by all its editions." },
                "editionRating": { "type": "integer", "description": "Only for the edition named in bookId: its own rating from 0 to 5, 0 to clear. Leave out unless the user names the edition." },
                "isFavorite": { "type": "boolean", "description": "Whether the book is a favourite, for all its editions." },
                "personalReview": { "type": "string", "description": "The user's own review of the book, for all its editions; an empty string clears it. Only ever the user's words." },
                "isFinished": { "type": "boolean", "description": "True marks it finished; false marks it not finished." }
              },
              "required": ["bookId"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                if (Id(args, "bookId") is not { } bookId)
                    return Invalid("'bookId' is required.");

                var op = new UpdateBookOp(
                    bookId,
                    Title: Str(args, "title"),
                    Author: Str(args, "author"),
                    Description: Str(args, "description"),
                    Rating: Num(args, "rating"),
                    IsFavorite: Bool(args, "isFavorite"),
                    PersonalReview: Str(args, "personalReview"),
                    IsFinished: Bool(args, "isFinished"),
                    EditionRating: Num(args, "editionRating"));
                if (op is { Title: null, Author: null, Description: null, Rating: null, IsFavorite: null, PersonalReview: null, IsFinished: null, EditionRating: null })
                    return Invalid("Give at least one field to change.");

                return await draft.StageAsync(op, [], ct);
            }),

        Stage(
            "topics_create_kind",
            "Creates a new topic kind. Propose a new kind only when many topics need it, and use a kind the library already has when one fits. Creating a kind always waits for the reader's review.",
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The kind's name, for example Person or Place. Required." },
                "colorKey": { "type": "string", "description": "One of kind-1 through kind-12. Omit it and Nostos takes the first free palette colour." }
              },
              "required": ["name"],
              "additionalProperties": true
            }
            """,
            async (draft, args, ct) =>
            {
                var name = Str(args, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return Invalid("'name' is required.");

                var colorKey = Str(args, "colorKey")?.Trim();
                if (colorKey is { Length: > 0 } && !TopicKindModel.IsValidColorKey(colorKey))
                    return Invalid("'colorKey' must be one of kind-1 through kind-12.");

                return await draft.StageAsync(
                    new CreateTopicKindOp(name, name, colorKey), [], ct);
            }),

        StageWithContext(
            "topics_set_kind",
            "Gives one topic kind to many topics, or clears their kind. A kind the reader set is kept unless the user named that topic in this request; the result says how many were kept. Name the topics the user named in namedTopicIds; leave a topic out when you are unsure.",
            $$"""
            {
              "type": "object",
              "properties": {
                "topicIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "description": "The topics to classify, up to 250 (one page of topics_list). Required." },
                "kind": { "type": "string", "description": "{{TopicKindArgument}} Omit it with clear true to remove the kind." },
                "clear": { "type": "boolean", "description": "True to remove the topics' kind instead of setting one." },
                "namedTopicIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "description": "Only the topicIds whose topic the user named in this request. A kind the reader set is replaced or cleared only for these. Omit when the user named no topic." }
              },
              "required": ["topicIds"],
              "additionalProperties": true
            }
            """,
            async (context, draft, args, ct) =>
            {
                if (!TryIds(args, "topicIds", out var topicIds) || topicIds is not { Count: > 0 })
                    return Invalid("'topicIds' must be a non-empty array of topic ids.");
                // One page of topics_list is at most 250 topics, so a batch
                // that matches a page classifies it in a single call while the
                // preview stays bounded.
                if (topicIds.Count > 250)
                    return Invalid("At most 250 topics can be classified in one call; ask for the next page and stage it too.");
                if (!TryIds(args, "namedTopicIds", out var namedTopicIds))
                    return Invalid("'namedTopicIds' must be an array of topic ids when it is given.");

                var clear = Bool(args, "clear") ?? false;
                TopicKindRef? kind = null;
                var dependsOn = new List<int>();
                if (!clear)
                {
                    var kindArg = Str(args, "kind");
                    if (kindArg is null)
                        return Invalid("'kind' is required unless 'clear' is true.");
                    if (!draft.TryResolveTopicKind(kindArg, out var resolved, out var createdBy))
                    {
                        var existing = (await topics.GetAllKindsAsync(ct))
                            .FirstOrDefault(candidate => string.Equals(
                                candidate.Name, kindArg.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (existing is null)
                        {
                            return AssistantToolResult.Fail(
                                LibraryChangesetErrorCodes.TopicKindNotFound,
                                $"'{kindArg}' is neither a kind id nor a kind created earlier in this turn. Use topics_list_kinds for the existing kinds.");
                        }
                        resolved = TopicKindRef.Existing(existing.Id);
                    }
                    kind = resolved;
                    if (createdBy is { } step)
                        dependsOn.Add(step);
                }

                // The reader's own kinds are theirs. Only a topic the user named
                // in their request may have one replaced or cleared. The model
                // may say which topics it believes the user named; each one is
                // checked against the reader's own words before it is trusted,
                // and without that list the words alone decide.
                var distinct = topicIds.Distinct().ToList();
                var states = await topics.GetByIdsAsync(distinct, ct);
                var byId = states.ToDictionary(topic => topic.Id);
                var userText = context.UserText ?? string.Empty;
                var named = namedTopicIds is null
                    ? null
                    : namedTopicIds.ToHashSet();
                var allowedToReplace = new List<Guid>();
                var overwrite = new List<Guid>();
                var keptReaderKinds = 0;
                var unknown = new List<Guid>();
                foreach (var topicId in distinct)
                {
                    if (!byId.TryGetValue(topicId, out var topic))
                    {
                        unknown.Add(topicId);
                        continue;
                    }

                    var readerKind = topic.KindSource == TopicKindSources.Reader;
                    if (readerKind)
                    {
                        var isNamed = named is not null
                            ? named.Contains(topicId) && AssistantNamedTopics.Names(userText, topic.Name)
                            : AssistantNamedTopics.Names(userText, topic.Name);
                        if (!isNamed)
                        {
                            keptReaderKinds++;
                            continue;
                        }
                        allowedToReplace.Add(topicId);
                    }

                    overwrite.Add(topicId);
                }

                if (unknown.Count > 0)
                {
                    return AssistantToolResult.Fail(
                        LibraryChangesetErrorCodes.TopicNotFound,
                        unknown.Count == 1
                            ? "One of the topics was not found. Use topics_list for the topics that exist."
                            : $"{unknown.Count} of the topics were not found. Use topics_list for the topics that exist.");
                }

                if (overwrite.Count == 0)
                {
                    return AssistantToolResult.Ok(Element(new
                    {
                        status = "kept_reader_kinds",
                        kept = keptReaderKinds,
                        note = keptReaderKinds == 1
                            ? "Kept 1 kind you set yourself."
                            : $"Kept {keptReaderKinds} kinds you set yourself.",
                    }));
                }

                // The op carries which topics were named, not a single flag, so
                // an apply recomputed later can never overwrite a reader kind
                // the reader did not name — a clear included.
                var stageArgs = new SetTopicKindOp(
                    overwrite,
                    kind,
                    OverwriteReaderKinds: allowedToReplace.Count > 0,
                    NamedTopicIds: allowedToReplace);
                var staged = await draft.StageAsync(stageArgs, dependsOn, ct);
                if (!staged.Success || keptReaderKinds == 0)
                    return staged;

                return staged with
                {
                    Data = WithReaderKindNote(staged.Data, keptReaderKinds),
                };
            }),
    ];

    private static JsonElement? WithReaderKindNote(JsonElement? data, int kept)
    {
        if (data is not { } value)
            return data;

        if (JsonNode.Parse(value.GetRawText()) is not JsonObject node)
            return data;

        node["keptReaderKinds"] = kept;
        node["note"] = kept == 1
            ? "Kept 1 kind you set yourself."
            : $"Kept {kept} kinds you set yourself.";
        return JsonSerializer.SerializeToElement(node);
    }

    private static AssistantCapability Stage(
        string name,
        string summary,
        string schema,
        Func<AssistantChangesetDraft, JsonElement, CancellationToken, Task<AssistantToolResult>> stage) =>
        new(
            name,
            AssistantTrustClass.Stage,
            AssistantCapabilityCategory.Organization,
            summary,
            schema,
            (context, args, ct) => stage(context.Changeset!, args, ct));

    private static AssistantCapability StageWithContext(
        string name,
        string summary,
        string schema,
        Func<AssistantToolContext, AssistantChangesetDraft, JsonElement, CancellationToken, Task<AssistantToolResult>> stage) =>
        new(
            name,
            AssistantTrustClass.Stage,
            AssistantCapabilityCategory.Organization,
            summary,
            schema,
            (context, args, ct) => stage(context, context.Changeset!, args, ct));

    private static bool TryCollection(
        AssistantChangesetDraft draft,
        JsonElement args,
        string name,
        out LibraryCollectionRef collection,
        out List<int> dependsOn,
        out AssistantToolResult? failure)
    {
        dependsOn = [];
        failure = null;
        var value = Str(args, name) ?? Str(args, name + "Id");
        if (!draft.TryResolveCollection(value, out collection, out var createdBy))
        {
            failure = value is null ? Invalid($"'{name}' is required.") : UnknownCollection(value);
            return false;
        }

        if (createdBy is { } step)
            dependsOn.Add(step);
        return true;
    }

    private static AssistantToolResult UnknownCollection(string? value) =>
        AssistantToolResult.Fail(
            LibraryChangesetErrorCodes.CollectionNotFound,
            $"'{value}' is neither a collection id nor a collection created earlier in this turn. Use library_list_collections for ids.");
}
