using Nostos.Backend.Services.Library.Imports;

namespace Nostos.Backend.Services.Calibre;

/// <summary>
/// SelfHosted has one library, so every upload uses the same configured cap.
/// A hosted deployment replaces this with a per-plan provider; the upload,
/// extraction and cleanup code never changes.
/// </summary>
public sealed class SelfHostedCalibreUploadLimitsProvider(CalibreUploadOptions options)
    : ICalibreUploadLimitsProvider
{
    public CalibreUploadLimits For(string ownerKey) => options.ToLimits();
}

/// <summary>SelfHosted is single-user: every request sees the same scratch area.</summary>
public sealed class SelfHostedCalibreUploadOwnerProvider(CalibreUploadOwnerScope ownerScope)
    : ICalibreUploadOwnerProvider
{
    public const string OwnerKey = "selfhosted";

    public string GetOwnerKey() => ownerScope.Current ?? OwnerKey;
}
