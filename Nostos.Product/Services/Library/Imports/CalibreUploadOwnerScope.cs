namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Carries the upload owner from the HTTP request into the background import
/// scope that runs after the response (#1572). A host resolves its tenant or
/// user in <see cref="ICalibreUploadOwnerProvider"/>; the job enters the same
/// owner here so a staged library can never be read by another tenant's job.
/// SelfHosted has one owner and never needs to change it.
/// </summary>
public sealed class CalibreUploadOwnerScope
{
    private readonly AsyncLocal<string?> _owner = new();

    public string? Current => _owner.Value;

    public IDisposable Enter(string ownerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        var previous = _owner.Value;
        _owner.Value = ownerKey;
        return new Restore(this, previous);
    }

    private sealed class Restore(CalibreUploadOwnerScope scope, string? previous) : IDisposable
    {
        public void Dispose() => scope._owner.Value = previous;
    }
}
