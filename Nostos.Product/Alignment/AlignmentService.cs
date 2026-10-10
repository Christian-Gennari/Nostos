using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;

namespace Nostos.Product.Alignment;

public sealed class AlignmentException(
    string code,
    int statusCode,
    string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class AlignmentOptions
{
    public const string SectionName = "Alignment";
    public double ClipSeconds { get; set; } = 20;
}

public sealed record AlignmentLocator(
    int SpineIndex,
    string ResourceHref,
    string? Cfi,
    int? TextOffset);

public sealed record AlignmentLocateResult(
    bool Located,
    string? Reason,
    double? Score,
    string? Excerpt,
    AlignmentLocator? Locator)
{
    public static AlignmentLocateResult Declined(string reason) =>
        new(false, reason, null, null, null);
}

public interface IAlignmentService
{
    Task<AlignmentLocateResult> LocateAsync(
        AudioBookModel source,
        EBookModel target,
        double positionSeconds,
        CancellationToken ct = default);
}

public sealed class AlignmentService(
    IAudioClipCutter cutter,
    IAlignmentTranscriber transcriber,
    IBookAssetStorage storage,
    IEnumerable<IBookTextExtractor> extractors,
    AlignmentDocumentCache cache,
    AlignmentOptions options,
    IAiUsageAccountingService usage) : IAlignmentService
{
    public async Task<AlignmentLocateResult> LocateAsync(
        AudioBookModel source,
        EBookModel target,
        double positionSeconds,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds <= 0)
        {
            throw new AlignmentException(
                AudioClipErrorCodes.InvalidPosition,
                400,
                "A positive, finite audiobook position is required.");
        }

        if (!source.FileDetails.HasFile)
        {
            throw new AlignmentException(
                "alignment_source_media_missing",
                422,
                "The audiobook has no stored audio.");
        }

        if (!target.FileDetails.HasFile ||
            !string.Equals(
                Path.GetExtension(target.FileDetails.FileName),
                ".epub",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AlignmentException(
                "alignment_target_not_epub",
                422,
                "The target must be an EPUB with stored media.");
        }

        if (source.WorkId == Guid.Empty || source.WorkId != target.WorkId)
        {
            throw new AlignmentException(
                "alignment_work_mismatch",
                422,
                "The two editions do not belong to the same work.");
        }

        if (!await transcriber.IsAvailableAsync(ct))
        {
            throw new AlignmentException(
                "alignment_transcription_unavailable",
                503,
                "Alignment transcription is not configured.");
        }

        var extractor = extractors.Single(
            x => x.Format == BookTextSourceFormat.Epub);

        // Do all local preparation before reserving provider usage.
        var document = await cache.GetAsync(
            target.Id, storage, extractor, ct);

        var matcher = new PassageMatcher(document);

        await using var clip = await cutter.CutAsync(
            source, positionSeconds, options.ClipSeconds, ct);

        AiUsageLease? lease;

        try
        {
            lease = await usage.BeginSttAsync(ct);
        }
        catch (AiUsageException)
        {
            throw;
        }

        SttResult transcription;

        try
        {
            transcription = await transcriber.TranscribeAsync(
                clip.Audio,
                "alignment.wav",
                "audio/wav",
                string.IsNullOrWhiteSpace(source.Metadata.Language)
                    ? null
                    : source.Metadata.Language.Trim(),
                ct);
        }
        catch (OperationCanceledException)
        {
            await usage.CompleteSttAsync(
                lease,
                new TranscriptionUsage(1, null, "Cancelled"),
                CancellationToken.None);
            throw;
        }
        catch
        {
            await usage.CompleteSttAsync(
                lease,
                new TranscriptionUsage(1, null, "ProviderError"),
                CancellationToken.None);
            throw;
        }

        await usage.CompleteSttAsync(
            lease,
            new TranscriptionUsage(
                1, transcription.DurationSeconds, "Completed"),
            CancellationToken.None);

        // Matching is local. A rejection is a successful transcription
        // and consumes no additional provider request.
        var match = matcher.Locate(transcription.Text, new MatcherSettings());

        if (!match.Accepted)
            return AlignmentLocateResult.Declined(match.Reason);

        if (match.End?.Locator is not EpubBookTextSourceLocator end)
        {
            throw new AlignmentException(
                "alignment_locator_invalid",
                422,
                "The matched passage has no EPUB end locator.");
        }

        return new AlignmentLocateResult(
            Located: true,
            Reason: null,
            Score: match.Score,
            Excerpt: match.Passage,
            Locator: new AlignmentLocator(
                end.SpineIndex,
                end.ResourceHref,
                end.Cfi,
                end.EndTextOffset));
    }
}
