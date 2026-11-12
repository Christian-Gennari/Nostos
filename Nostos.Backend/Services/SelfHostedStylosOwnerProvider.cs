using Nostos.Backend.Services.Stylos;

namespace Nostos.Backend.Services;

/// <summary>
/// Owner scope for the Stylos Sync Protocol on the SelfHosted host, which has
/// exactly one local owner. A host with accounts registers a provider that
/// answers from the signed-in person instead; every device and file is scoped
/// by the key, so sessions never leak across accounts.
/// </summary>
public sealed class SelfHostedStylosOwnerProvider : IStylosOwnerProvider
{
    internal const string OwnerKey = "selfhosted";

    public string GetOwnerKey() => OwnerKey;
}
