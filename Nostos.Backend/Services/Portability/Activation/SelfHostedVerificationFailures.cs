namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// The activation and recovery-restore coordinators report a verification failure
/// by its first specific code, so an operator sees the same vocabulary the
/// verifier produced instead of a generic failure. One implementation keeps the
/// two switch paths reporting identically.
/// </summary>
internal static class SelfHostedVerificationFailures
{
    private const string Unknown = "portable_verify_failed";

    internal static string FirstCode(IReadOnlyList<PortableLibraryVerificationFailure> failures) =>
        failures.Count == 0 ? Unknown : failures[0].Code;
}
