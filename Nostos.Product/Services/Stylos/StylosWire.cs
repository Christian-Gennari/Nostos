using System.Text.Json.Serialization;

namespace Nostos.Backend.Services.Stylos;

/// <summary>
/// Wire bodies of the Stylos Sync Protocol v1, taken field for field from the
/// <c>stylos-protocol</c> crate (<c>types.rs</c>). The crate's serde field names
/// are the wire format, so every field carries an explicit
/// <see cref="JsonPropertyNameAttribute"/>; the app's default camelCase policy
/// would otherwise turn <c>file_id</c> into <c>fileId</c>.
///
/// Optional fields the crate skips when absent are skipped here too
/// (<c>token</c>, <c>server_cert_sha256</c>, <c>server_name</c>,
/// <c>server_file_id</c>), so a device's serde sees exactly the shapes its own
/// unit tests pin.
/// </summary>
public static class StylosWire
{
    public const string ProtocolVersion = "1";
}

/// <summary>Body of <c>POST /stylos/v1/pair/start</c>.</summary>
public sealed record StylosPairStartRequest(
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("device_pubkey")] string DevicePublicKey);

/// <summary>Answer to <c>POST /stylos/v1/pair/start</c>.</summary>
public sealed record StylosPairStartResponse(
    [property: JsonPropertyName("pair_id")] string PairId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("expires_at")] long ExpiresAt);

/// <summary>The three states a polling device understands.</summary>
public static class StylosPairStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Expired = "expired";
}

/// <summary>Answer to <c>GET /stylos/v1/pair/{pair_id}</c>.</summary>
public sealed record StylosPairStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("token")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Token,
    [property: JsonPropertyName("server_cert_sha256")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ServerCertSha256,
    [property: JsonPropertyName("server_name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ServerName);

/// <summary>One file in a change-list answer.</summary>
public sealed record StylosFileEntry(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("rev")] long Rev,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("modified_at")] long ModifiedAt);

/// <summary>Answer to <c>GET /stylos/v1/files?since=</c>.</summary>
public sealed record StylosFileList(
    [property: JsonPropertyName("files")] IReadOnlyList<StylosFileEntry> Files,
    [property: JsonPropertyName("cursor")] string Cursor);

/// <summary>Answer to <c>GET /stylos/v1/files/{file_id}</c>.</summary>
public sealed record StylosFileContent(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("rev")] long Rev,
    [property: JsonPropertyName("content")] string Content);

/// <summary>Body of <c>PUT /stylos/v1/files/{file_id}</c>.</summary>
public sealed record StylosPutFileRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("base_rev")] long? BaseRev);

/// <summary>Successful <c>PUT</c> answer.</summary>
public sealed record StylosPutFileResponse(
    [property: JsonPropertyName("rev")] long Rev,
    [property: JsonPropertyName("hash")] string Hash);

/// <summary>Successful <c>DELETE</c> answer.</summary>
public sealed record StylosDeleteResponse(
    [property: JsonPropertyName("rev")] long Rev);

/// <summary>The server's current version, sent with a 409.</summary>
public sealed record StylosConflictInfo(
    [property: JsonPropertyName("server_rev")] long ServerRev,
    [property: JsonPropertyName("server_hash")] string ServerHash,
    [property: JsonPropertyName("server_file_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ServerFileId);

/// <summary>Body of a 409 answer.</summary>
public sealed record StylosConflictResponse(
    [property: JsonPropertyName("conflict")] StylosConflictInfo Conflict);

/// <summary>Body of every 4xx/5xx answer the protocol defines.</summary>
public sealed record StylosErrorResponse(
    [property: JsonPropertyName("error")] string Error);

// ---------------------------------------------------------------------------
// Library (Nostos only; the shape of a note the device can quote offline).
// The protocol crate deliberately does not type /library, so the names below
// are the spec's own: { id, kind, book_id, text, note, location, topics,
// citation: { markdown, url } } inside { books, items, deleted, cursor }.
// ---------------------------------------------------------------------------

public sealed record StylosLibraryCitation(
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("url")] string? Url);

public sealed record StylosLibraryItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("book_id")] string BookId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("topics")] IReadOnlyList<string> Topics,
    [property: JsonPropertyName("citation")] StylosLibraryCitation Citation);

public sealed record StylosLibraryBook(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author);

/// <summary>Answer to <c>GET /stylos/v1/library?since=</c>.</summary>
public sealed record StylosLibraryPage(
    [property: JsonPropertyName("books")] IReadOnlyList<StylosLibraryBook> Books,
    [property: JsonPropertyName("items")] IReadOnlyList<StylosLibraryItem> Items,
    [property: JsonPropertyName("deleted")] IReadOnlyList<string> Deleted,
    [property: JsonPropertyName("cursor")] string Cursor);
