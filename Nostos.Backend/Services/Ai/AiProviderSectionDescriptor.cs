using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Per-section column accessors and fallback metadata so settings persistence
/// and effective config resolution share one implementation across all five
/// provider surfaces without altering stored column semantics.
/// </summary>
internal sealed class AiProviderSectionDescriptor(
    AiProviderKind kind,
    string surface,
    Func<AiProviderSettingsModel, bool?> getEnabled,
    Action<AiProviderSettingsModel, bool?> setEnabled,
    Func<AiProviderSettingsModel, string?> getBaseUrl,
    Action<AiProviderSettingsModel, string?> setBaseUrl,
    Func<AiProviderSettingsModel, string?> getModel,
    Action<AiProviderSettingsModel, string?> setModel,
    Func<AiProviderSettingsModel, string?> getApiKeyEncrypted,
    Action<AiProviderSettingsModel, string?> setApiKeyEncrypted,
    bool fallbackEnabled,
    string fallbackBaseUrl,
    string fallbackModel,
    string apiKeyEnvironmentVariable)
{
    public AiProviderKind Kind { get; } = kind;
    public string Surface { get; } = surface;
    public Func<AiProviderSettingsModel, bool?> GetEnabled { get; } = getEnabled;
    public Action<AiProviderSettingsModel, bool?> SetEnabled { get; } = setEnabled;
    public Func<AiProviderSettingsModel, string?> GetBaseUrl { get; } = getBaseUrl;
    public Action<AiProviderSettingsModel, string?> SetBaseUrl { get; } = setBaseUrl;
    public Func<AiProviderSettingsModel, string?> GetModel { get; } = getModel;
    public Action<AiProviderSettingsModel, string?> SetModel { get; } = setModel;
    public Func<AiProviderSettingsModel, string?> GetApiKeyEncrypted { get; } = getApiKeyEncrypted;
    public Action<AiProviderSettingsModel, string?> SetApiKeyEncrypted { get; } = setApiKeyEncrypted;
    public bool FallbackEnabled { get; } = fallbackEnabled;
    public string FallbackBaseUrl { get; } = fallbackBaseUrl;
    public string FallbackModel { get; } = fallbackModel;
    public string ApiKeyEnvironmentVariable { get; } = apiKeyEnvironmentVariable;
}
