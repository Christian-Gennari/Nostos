namespace Nostos.Product.Http;

/// <summary>
/// Product-level request limits that protect parsing/storage work regardless of
/// which host applies additional abuse/rate-limit policy around the endpoint.
/// </summary>
public static class NostosProductRequestLimits
{
    public const long MaxCoverUploadBytes = 25L * 1024 * 1024;
    public const long MaxCoverRequestBytes = 26L * 1024 * 1024;

    /// <summary>The largest file the add-book form may send just to have its metadata read.</summary>
    public const long MaxFileMetadataRequestBytes = 60L * 1024 * 1024;
}
