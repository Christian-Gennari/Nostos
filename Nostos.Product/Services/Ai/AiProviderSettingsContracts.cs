using System.Text.Json.Serialization;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Host-supplied AI provider settings surface. SelfHosted stores BYOK settings;
/// official Cloud supplies a managed implementation. Product endpoints depend
/// only on this contract.
/// </summary>
public interface IAiProviderSettingsService : IAiProviderConfigResolver
{
    Task<AiProviderSettingsResponse> GetAsync(CancellationToken ct = default);

    Task<AiProviderSettingsResponse> UpdateAsync(
        AiProviderSettingsUpdateRequest request,
        CancellationToken ct = default);

    Task<AiProviderModelsResponse> ListModelsAsync(
        AiProviderModelsRequest request,
        CancellationToken ct = default);

    Task<AiProviderTestResult> TestAsync(
        AiProviderTestRequest request,
        CancellationToken ct = default);
}

public sealed record AiProviderSectionDto(
    bool Enabled,
    string BaseUrl,
    string Model,
    bool HasKey,
    bool KeyFromServerEnv);

public sealed record AlignmentTranscriptionSectionDto(
    bool Enabled,
    string BaseUrl,
    string Model,
    bool HasKey,
    bool KeyFromServerEnv,
    bool UseVoiceTranscription = true,
    string ApiStyle = AlignmentTranscriptionApiStyles.OpenAiMultipart);

/// <summary>
/// The optional surfaces are appended to preserve positional construction
/// compatibility with hosts that only provide LLM and voice STT settings.
/// </summary>
public sealed record AiProviderSettingsResponse(
    AiProviderSectionDto Llm,
    AiProviderSectionDto Stt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AiProviderSectionDto? Embedding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AlignmentTranscriptionSectionDto? AlignmentTranscription = null);

public sealed record AiProviderSectionUpdate(
    bool? Enabled,
    string? BaseUrl,
    string? Model,
    string? ApiKey);

public sealed record AlignmentTranscriptionSectionUpdate(
    bool? Enabled,
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    bool? UseVoiceTranscription = null,
    string? ApiStyle = null);

public sealed record AiProviderSettingsUpdateRequest(
    AiProviderSectionUpdate? Llm,
    AiProviderSectionUpdate? Stt,
    AiProviderSectionUpdate? Embedding = null,
    AlignmentTranscriptionSectionUpdate? AlignmentTranscription = null);

public sealed record AiProviderModelsRequest(
    string Kind,
    string? BaseUrl,
    string? ApiKey);

public sealed record AiProviderModelsResponse(IReadOnlyList<string> Models);

/// <summary>
/// <paramref name="ApiStyle"/> and <paramref name="UseVoiceTranscription"/>
/// apply to the alignment section only: they let the form test what the user
/// has selected before it is saved. Omitted, the saved values are used.
/// </summary>
public sealed record AiProviderTestRequest(
    string Kind,
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    string? ApiStyle = null,
    bool? UseVoiceTranscription = null);

public sealed record AiProviderTestResult(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error);

/// <summary>
/// A host owns provider identity/configuration and therefore refuses customer
/// configuration changes. The product maps this to the stable 403 contract.
/// </summary>
public sealed class AiProviderConfigurationManagedException(string message) : Exception(message);

/// <summary>
/// A host owns provider identity/configuration and therefore refuses customer
/// configuration changes. The product maps this to the stable 403 contract.
/// </summary>
public sealed class AiProviderConfigurationOwnedByHostException(string message) : Exception(message);
