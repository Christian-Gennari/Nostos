using Nostos.Backend.Services.Carrel;

namespace Nostos.Backend.Services;

/// <summary>
/// Owner scope for the Carrel Sync Protocol on the SelfHosted host, which has
/// exactly one local owner. A host with accounts registers a provider that
/// answers from the signed-in person instead; every device and file is scoped
/// by the key, so sessions never leak across accounts.
/// </summary>
public sealed class SelfHostedCarrelOwnerProvider : ICarrelOwnerProvider
{
    internal const string OwnerKey = "selfhosted";

    public string GetOwnerKey() => OwnerKey;
}
