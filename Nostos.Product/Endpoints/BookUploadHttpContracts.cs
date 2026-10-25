using Nostos.Backend.Services.BookUpload;

namespace Nostos.Backend.Endpoints;

/// <summary>Creation payload for a resumable book upload (#1266).</summary>
public sealed record BookUploadCreateHttpRequest(
    long TotalBytes,
    string? FileName,
    string? ContentType,
    string? Sha256,
    string? ClientFingerprint = null);

/// <summary>Stable, safe error body. Never carries exception text or paths.</summary>
public sealed record BookUploadErrorResponse(string Error, string Message);

public static class BookUploadHttpErrors
{
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        [BookUploadException.NotFound] = "The upload session was not found.",
        [BookUploadException.BookNotFound] = "The book was not found.",
        [BookUploadException.InvalidRequest] = "The upload request is not valid.",
        [BookUploadException.UnsupportedType] = "This file type cannot be uploaded as a book.",
        [BookUploadException.InvalidState] = "The upload session is not in a state that allows this operation.",
        [BookUploadException.InProgress] = "This book's current import or upload must finish first.",
        [BookUploadException.PartConflict] = "A different part was already accepted for this index.",
        [BookUploadException.PartHashMismatch] = "The part did not match its checksum. It will be sent again.",
        [BookUploadException.PartRangeInvalid] = "The part range does not match this upload session.",
        [BookUploadException.FileHashMismatch] = "The finished file did not match its checksum.",
        [BookUploadException.Expired] = "This upload session expired. Choose the file again to resume.",
        [BookUploadException.StorageExhausted] = "There is not enough free storage for this file.",
    };

    public static string MessageFor(string code) => Messages.GetValueOrDefault(code, "The upload failed.");

    public static IResult From(BookUploadException exception) => exception.Code switch
    {
        BookUploadException.NotFound or BookUploadException.BookNotFound =>
            Result(exception.Code, StatusCodes.Status404NotFound),
        BookUploadException.Expired => Result(exception.Code, StatusCodes.Status410Gone),
        BookUploadException.InvalidState
            or BookUploadException.InProgress
            or BookUploadException.PartConflict =>
            Result(exception.Code, StatusCodes.Status409Conflict),
        BookUploadException.PartHashMismatch or BookUploadException.FileHashMismatch =>
            Result(exception.Code, StatusCodes.Status422UnprocessableEntity),
        BookUploadException.PartRangeInvalid =>
            Result(exception.Code, StatusCodes.Status416RangeNotSatisfiable),
        BookUploadException.StorageExhausted =>
            Result(exception.Code, StatusCodes.Status507InsufficientStorage),
        BookUploadException.UnsupportedType or BookUploadException.InvalidRequest =>
            Result(exception.Code, StatusCodes.Status400BadRequest),
        _ => Result(exception.Code, StatusCodes.Status409Conflict),
    };

    public static IResult Result(string code, int statusCode) =>
        Results.Json(new BookUploadErrorResponse(code, MessageFor(code)), statusCode: statusCode);

    public static IResult InvalidRequest() =>
        Result(BookUploadException.InvalidRequest, StatusCodes.Status400BadRequest);

    public static IResult PartTooLarge() =>
        Results.Json(
            new BookUploadErrorResponse(
                BookUploadException.InvalidRequest,
                "The part request body exceeds the maximum accepted size."),
            statusCode: StatusCodes.Status413PayloadTooLarge);
}
