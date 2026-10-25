using Nostos.Backend.Services.BookUpload;

namespace Nostos.Backend.Services;

/// <summary>
/// Owner scope for book uploads on the SelfHosted host, which has exactly one
/// local owner. A host with accounts replaces this registration with the
/// authenticated account claim; the upload service treats any other owner as
/// not-found, so sessions never leak across accounts.
/// </summary>
public sealed class SelfHostedBookUploadOwnerProvider : IBookUploadOwnerProvider
{
    internal const string OwnerKey = "selfhosted";

    public string GetOwnerKey() => OwnerKey;
}
