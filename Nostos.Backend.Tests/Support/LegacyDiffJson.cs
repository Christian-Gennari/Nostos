using System.Text.Json;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// A changeset diff exactly as a host that predates the topic-kind fields
/// serialises it: no <c>topicKinds</c> and no <c>topicKindAssignments</c>. Every
/// consumer of a stored or replayed diff must read this without throwing
/// (issue #1770 review, round 3).
/// </summary>
public static class LegacyDiffJson
{
    public const string Json = """
        {
          "collections": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "kind": "created",
              "name": "Fiction",
              "previousName": null,
              "parentName": null,
              "previousParentName": null,
              "parentChanged": false,
              "bookCount": 1
            }
          ],
          "memberships": [
            {
              "bookId": "22222222-2222-2222-2222-222222222222",
              "title": "A Book",
              "author": null,
              "added": ["Fiction"],
              "removed": []
            }
          ],
          "bookEdits": [],
          "counts": {
            "collectionsCreated": 1,
            "collectionsDeleted": 0,
            "collectionsUpdated": 0,
            "booksTouched": 1,
            "membershipsAdded": 1,
            "membershipsRemoved": 0,
            "booksLosingMembership": 0,
            "bookEdits": 0
          }
        }
        """;

    /// <summary>The same JSON, read the way the server reads a host's body.</summary>
    public static LibraryChangesetDiffDto Read() =>
        JsonSerializer.Deserialize<LibraryChangesetDiffDto>(Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
