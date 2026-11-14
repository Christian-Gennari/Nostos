namespace Nostos.Backend.Endpoints;

/// <summary>
/// Log category for book-file upload interruptions, shared by the legacy
/// whole-request upload and the resumable parts route.
///
/// A marker type rather than <c>ILogger&lt;T&gt;</c> over one of the endpoint
/// classes: those are static, cannot be type arguments, and both routes used to
/// log under one category name, which a single marker keeps.
/// </summary>
internal sealed class BookUploadLog;
