using Microsoft.AspNetCore.Http;

namespace Nostos.Product.Http;

/// <summary>The <c>{ "error": "message" }</c> body used by notes, topics and the imports.</summary>
public sealed record ApiErrorBody(string Error);

/// <summary>The <c>{ "error": "code", "message": "message" }</c> body used by uploads.</summary>
public sealed record ApiCodeAndMessageBody(string Error, string Message);

/// <summary>The <c>{ "code": "code", "message": "message" }</c> body used by upload conflicts.</summary>
public sealed record ApiCodeMessageBody(string Code, string Message);

/// <summary>
/// The error bodies the Minimal API endpoint groups return, one factory per wire
/// shape.
///
/// This is deliberately NOT one new shape. Issue #1632 asked for a single helper
/// that PRODUCES the shapes each route already returns; Nostos-Cloud and the
/// Angular client parse these bytes, so the refactor must be invisible on the
/// wire. The shapes, and who returns them, are:
///
/// <list type="bullet">
/// <item><see cref="Problem"/> — RFC 9110 Problem Details. The library domain
/// (<c>LibraryHttpMapper</c>), alignment and note reprocessing put the machine
/// code in <c>title</c> and the reply in <c>detail</c>; the reading list repeats
/// the code in a <c>code</c> extension; the ISBN lookup uses a human <c>title</c>
/// plus the <c>code</c> extension.</item>
/// <item><see cref="Error"/> — <c>{ "error": "message" }</c>: notes, topics,
/// note processing, the reading-history and highlight imports.</item>
/// <item><see cref="ErrorWithCode"/> — <c>{ "error": "code", "message": ... }</c>:
/// resumable and legacy book uploads.</item>
/// <item><see cref="CodeMessage"/> — <c>{ "code": "code", "message": ... }</c>:
/// book-file upload conflicts and the Calibre guard.</item>
/// <item><see cref="Message"/> and <see cref="EmptyBadRequest"/> — a bare JSON
/// string, or no body at all: writings' own rejections.</item>
/// </list>
///
/// Two routes returning different shapes for the same kind of error is a
/// follow-up decision for the owner, not something this helper should settle.
/// </summary>
public static class ApiErrors
{
    /// <summary>
    /// Problem Details. <paramref name="title"/> is the machine code for the
    /// library, alignment and reprocessing routes; pass <paramref name="code"/>
    /// only where the route also repeats it in a <c>code</c> extension.
    /// </summary>
    public static IResult Problem(
        int statusCode,
        string title,
        string? detail,
        string? code = null) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: code is null
                ? null
                : new Dictionary<string, object?> { ["code"] = code });

    /// <summary><c>{ "error": "message" }</c>; a 400 unless a route says otherwise.</summary>
    public static IResult Error(string message, int statusCode = StatusCodes.Status400BadRequest) =>
        Results.Json(new ApiErrorBody(message), statusCode: statusCode);

    /// <summary><c>{ "error": "code", "message": "message" }</c> — the upload contract.</summary>
    public static IResult ErrorWithCode(string code, string message, int statusCode) =>
        Results.Json(new ApiCodeAndMessageBody(code, message), statusCode: statusCode);

    /// <summary><c>{ "code": "code", "message": "message" }</c> — upload conflicts.</summary>
    public static IResult CodeMessage(string code, string message, int statusCode) =>
        Results.Json(new ApiCodeMessageBody(code, message), statusCode: statusCode);

    /// <summary>
    /// A 400 whose body is the message as a bare JSON string. Writings answer
    /// their own rejections this way; clients already read it, so it stays.
    /// </summary>
    public static IResult Message(string? message) => Results.BadRequest(message);

    /// <summary>
    /// A 400 with no body of its own; <c>UseStatusCodePages</c> turns it into the
    /// pipeline's Problem Details. Writings return this for a body that does not
    /// parse.
    /// </summary>
    public static IResult EmptyBadRequest() => Results.BadRequest();
}
