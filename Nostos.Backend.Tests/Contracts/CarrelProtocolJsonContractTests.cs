using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nostos.Backend.Services.Carrel;
using Xunit;

namespace Nostos.Backend.Tests.Contracts;

/// <summary>
/// The wire contract of Carrel Sync Protocol v1, asserted against the exact
/// JSON the <c>carrel-protocol</c> crate's own tests pin (its serde field names
/// and enum spellings are the format). Nostos's default camelCase policy would
/// turn <c>file_id</c> into <c>fileId</c>, so every shape is checked literally.
/// </summary>
public sealed class CarrelProtocolJsonContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Web);

    private static JsonNode Json(string text) => JsonNode.Parse(text)!;

    private static void ShouldBeJson(string actual, string expected) =>
        JsonNode.DeepEquals(Json(actual), Json(expected)).Should().BeTrue(
            $"expected {expected} but got {actual}");

    [Fact]
    public void File_list_matches_the_crates_pinned_shape()
    {
        var list = new CarrelFileList(
            [
                new CarrelFileEntry(
                    "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
                    "notes/one.md",
                    "abc",
                    2,
                    false,
                    1_800_000_000),
            ],
            "7");

        // carrel-protocol/src/types.rs, file_list_shape_matches_spec.
        ShouldBeJson(Serialize(list), """
            {
              "files": [{
                "file_id": "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
                "path": "notes/one.md",
                "hash": "abc",
                "rev": 2,
                "deleted": false,
                "modified_at": 1800000000
              }],
              "cursor": "7"
            }
            """);
    }

    [Fact]
    public void Pair_start_matches_the_crates_pinned_shape()
    {
        var response = new CarrelPairStartResponse(
            "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", "K7Q-4MZ", 1_800_000_000);

        ShouldBeJson(Serialize(response), """
            {
              "pair_id": "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
              "code": "K7Q-4MZ",
              "expires_at": 1800000000
            }
            """);

        var request = JsonSerializer.Deserialize<CarrelPairStartRequest>(
            """{"device_name":"X250","device_pubkey":"abab"}""", Web)!;
        request.DeviceName.Should().Be("X250");
        request.DevicePublicKey.Should().Be("abab");
    }

    [Fact]
    public void Pending_pairing_status_omits_the_token_and_the_certificate()
    {
        var pending = new CarrelPairStatusResponse(
            CarrelPairStatus.Pending, null, null, "study");

        // carrel-protocol/src/types.rs, pending_status_omits_secrets.
        ShouldBeJson(Serialize(pending), """
            {"status":"pending","server_name":"study"}
            """);

        var approved = new CarrelPairStatusResponse(
            CarrelPairStatus.Approved, "crl_token", null, null);
        ShouldBeJson(Serialize(approved), """
            {"status":"approved","token":"crl_token"}
            """);
    }

    [Fact]
    public void Conflict_matches_the_crates_pinned_shape()
    {
        var conflict = new CarrelConflictResponse(new CarrelConflictInfo(3, "def", null));

        // carrel-protocol/src/types.rs, conflict_shape_matches_spec.
        ShouldBeJson(Serialize(conflict), """
            {"conflict":{"server_rev":3,"server_hash":"def"}}
            """);

        var rename = new CarrelConflictResponse(new CarrelConflictInfo(
            3, "def", "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"));
        ShouldBeJson(Serialize(rename), """
            {"conflict":{"server_rev":3,"server_hash":"def","server_file_id":"0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"}}
            """);
    }

    [Fact]
    public void Put_request_accepts_a_null_or_numeric_base_rev()
    {
        var none = JsonSerializer.Deserialize<CarrelPutFileRequest>(
            """{"path":"a.md","content":"x","base_rev":null}""", Web)!;
        none.BaseRev.Should().BeNull();

        var some = JsonSerializer.Deserialize<CarrelPutFileRequest>(
            """{"path":"a.md","content":"x","base_rev":4}""", Web)!;
        some.BaseRev.Should().Be(4);

        // Option<u64> without skip_serializing: the crate writes null too.
        ShouldBeJson(Serialize(none), """{"path":"a.md","content":"x","base_rev":null}""");
    }

    [Fact]
    public void File_content_and_delete_response_use_the_crates_field_names()
    {
        var content = new CarrelFileContent(
            "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", "notes/one.md", "abc", 2, "# One\n");
        ShouldBeJson(Serialize(content), """
            {
              "file_id": "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
              "path": "notes/one.md",
              "hash": "abc",
              "rev": 2,
              "content": "# One\n"
            }
            """);

        ShouldBeJson(Serialize(new CarrelDeleteResponse(3)), """{"rev":3}""");
        ShouldBeJson(
            Serialize(new CarrelPutFileResponse(4, "def")), """{"rev":4,"hash":"def"}""");

        var serialized = Serialize(content);
        serialized.Should().Contain("\"file_id\"").And.NotContain("\"fileId\"");
    }

    [Fact]
    public void Errors_use_the_crates_error_body()
    {
        ShouldBeJson(
            Serialize(new CarrelErrorResponse("missing or invalid token")),
            """{"error":"missing or invalid token"}""");
    }

    [Fact]
    public void Library_items_carry_the_specs_field_names()
    {
        var page = new CarrelLibraryPage(
            [new CarrelLibraryBook("book", "Gilead", "Marilynne Robinson")],
            [
                new CarrelLibraryItem(
                    "note",
                    "highlight",
                    "book",
                    "Grace is everywhere.",
                    null,
                    "Page 12",
                    ["Attention"],
                    new CarrelLibraryCitation("Marilynne Robinson, *Gilead*, p. 12", "https://n/brain/notes/note")),
            ],
            ["gone"],
            "12");

        ShouldBeJson(Serialize(page), """
            {
              "books": [{"id":"book","title":"Gilead","author":"Marilynne Robinson"}],
              "items": [{
                "id":"note",
                "kind":"highlight",
                "book_id":"book",
                "text":"Grace is everywhere.",
                "note":null,
                "location":"Page 12",
                "topics":["Attention"],
                "citation":{
                  "markdown":"Marilynne Robinson, *Gilead*, p. 12",
                  "url":"https://n/brain/notes/note"
                }
              }],
              "deleted":["gone"],
              "cursor":"12"
            }
            """);
    }
}
