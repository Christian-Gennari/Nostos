namespace Nostos.Backend.Services.Stylos;

/// <summary>
/// Whose devices and drafts a Stylos request is about.
///
/// <para>The protocol has one account behind every device token, and the sync
/// routes never take a user id from the request: the token names the owner.
/// SelfHosted has exactly one local owner
/// (<see cref="SelfHostedStylosOwnerProvider"/>); a host with accounts
/// registers a provider that answers from the signed-in person, so one
/// person's paired device can never read another's drafts or notes.</para>
/// </summary>
public interface IStylosOwnerProvider
{
    string GetOwnerKey();
}
