using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Serialization;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Discovery;

namespace Nostos.Product.Composition;

internal static class NostosProductOptionsSetup
{
    internal static NostosProductDescriptor AddNostosProductOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var assistant = BindAndValidateAssistantOptions(configuration);
        services.AddSingleton(assistant);

        var speech = BindSpeechOptions(configuration);
        services.AddSingleton(speech);

        services.AddSingleton(BindEmbeddingOptions(configuration));

        var opds = BindAndNormalizeOpdsOptions(configuration);
        services.AddSingleton(opds);

        services.AddSingleton(BindBookTextOptions(configuration));

        var alignment = BindAndValidateAlignmentOptions(configuration);
        services.AddSingleton(alignment);

        services.AddSingleton(BindAndNormalizeLibraryReceiptRetentionOptions(configuration));

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
        });

        services.Configure<ReadwiseOptions>(
            configuration.GetSection(ReadwiseOptions.SectionName));
        services.Configure<AcquisitionOptions>(
            configuration.GetSection(AcquisitionOptions.SectionName));
        services.Configure<ProviderDiscoveryOptions>(
            configuration.GetSection(ProviderDiscoveryOptions.SectionName));

        return new NostosProductDescriptor(assistant, speech, opds);
    }

    internal static AssistantOptions BindAndValidateAssistantOptions(IConfiguration configuration)
    {
        var assistant =
            configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>()
            ?? new AssistantOptions();
        // The request timeout must fire before the whole-turn budget; otherwise a
        // stalled provider is reported as an execution limit (#1561).
        AssistantExecutionBudget.ValidateClocks(assistant);
        return assistant;
    }

    internal static SpeechOptions BindSpeechOptions(IConfiguration configuration) =>
        configuration.GetSection(SpeechOptions.SectionName).Get<SpeechOptions>()
        ?? new SpeechOptions();

    internal static EmbeddingOptions BindEmbeddingOptions(IConfiguration configuration) =>
        configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
        ?? new EmbeddingOptions();

    internal static BookTextOptions BindBookTextOptions(IConfiguration configuration) =>
        configuration.GetSection(BookTextOptions.SectionName).Get<BookTextOptions>()
        ?? new BookTextOptions();

    internal static OpdsOptions BindAndNormalizeOpdsOptions(IConfiguration configuration)
    {
        var opds =
            configuration.GetSection(OpdsOptions.SectionName).Get<OpdsOptions>()
            ?? new OpdsOptions();
        return NormalizeAndValidateOpdsOptions(opds);
    }

    internal static OpdsOptions NormalizeAndValidateOpdsOptions(OpdsOptions opds)
    {
        opds.PageSize = OpdsOptions.NormalizePageSize(opds.PageSize);
        if (!OpdsOptions.TryNormalizePublicBaseUrl(
                opds.PublicBaseUrl,
                out var publicBaseUrl,
                out var publicBaseUrlError))
        {
            throw new InvalidOperationException(
                $"OPDS is enabled but 'Opds:PublicBaseUrl' is invalid ({publicBaseUrlError}). " +
                "Fix the URL, or unset it to derive the origin from each request.");
        }

        opds.PublicBaseUrl = publicBaseUrl;
        return opds;
    }

    internal static AlignmentOptions BindAndValidateAlignmentOptions(IConfiguration configuration)
    {
        var alignment =
            configuration.GetSection(AlignmentOptions.SectionName).Get<AlignmentOptions>()
            ?? new AlignmentOptions();
        return ValidateAndNormalizeAlignmentOptions(alignment);
    }

    internal static AlignmentOptions ValidateAndNormalizeAlignmentOptions(AlignmentOptions alignment)
    {
        if (!double.IsFinite(alignment.ClipSeconds) ||
            alignment.ClipSeconds <= 0 || alignment.ClipSeconds > 60)
        {
            throw new InvalidOperationException(
                "Alignment:ClipSeconds must be between 0 and 60.");
        }

        if (alignment.MaxEpubBytes <= 0)
        {
            throw new InvalidOperationException(
                "Alignment:MaxEpubBytes must be positive.");
        }

        if (alignment.MaxCachedCharacters <= 0)
        {
            throw new InvalidOperationException(
                "Alignment:MaxCachedCharacters must be positive.");
        }

        // Normalize the reverse search's maximum per-request spend
        // and the length of each probe before validating dependent
        // settings or registering the singleton.
        alignment.NormalizeProbeSettings();

        if (alignment.MatchToleranceCharacters < 0 ||
            alignment.MatchToleranceCharacters > 500)
        {
            throw new InvalidOperationException(
                "Alignment:MatchToleranceCharacters must be between 0 and 500.");
        }

        if (alignment.ConsistencyToleranceCharacters < 0 ||
            alignment.ConsistencyToleranceCharacters > 500)
        {
            throw new InvalidOperationException(
                "Alignment:ConsistencyToleranceCharacters must be between 0 and 500.");
        }

        if (!double.IsFinite(alignment.LeadInSeconds) ||
            alignment.LeadInSeconds < 0 ||
            alignment.LeadInSeconds > alignment.ProbeSeconds)
        {
            throw new InvalidOperationException(
                "Alignment:LeadInSeconds must be nonnegative and no larger than ProbeSeconds.");
        }

        if (!double.IsFinite(alignment.MaximumLocalRateRatio) ||
            alignment.MaximumLocalRateRatio < 1 ||
            alignment.MaximumLocalRateRatio > 100)
        {
            throw new InvalidOperationException(
                "Alignment:MaximumLocalRateRatio must be between 1 and 100.");
        }

        return alignment;
    }

    internal static LibraryReceiptRetentionOptions BindAndNormalizeLibraryReceiptRetentionOptions(
        IConfiguration configuration) =>
        LibraryReceiptRetentionOptions.Normalize(
            configuration.GetSection("LibraryReceiptRetention").Get<LibraryReceiptRetentionOptions>()
            ?? new LibraryReceiptRetentionOptions());
}
