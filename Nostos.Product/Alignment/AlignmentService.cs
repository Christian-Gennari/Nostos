using Microsoft.Extensions.Logging;
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

    public long MaxEpubBytes { get; set; } =
        200L * 1024 * 1024;

    public long MaxCachedCharacters { get; set; } =
        20_000_000;
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
    AlignmentLocator? Locator,
    bool AnchorStored = false)
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
    IAlignmentDocumentProvider cache,
    AlignmentOptions options,
    IAiUsageAccountingService usage,
    IAlignmentAnchorStore anchors,
    IAlignmentAudioRevisionProvider audioRevisions,
    ILogger<AlignmentService> logger) : IAlignmentService
{
    public async Task<AlignmentLocateResult> LocateAsync(
        AudioBookModel source,
        EBookModel target,
        double positionSeconds,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(positionSeconds) ||
            positionSeconds <= 0)
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

        if (source.WorkId == Guid.Empty ||
            source.WorkId != target.WorkId)
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

        // The document and revision belong to the same storage read.
        var snapshot = await cache.GetAsync(
            target.Id, storage, extractor, ct);

        var matcher = new PassageMatcher(snapshot.Document);

        await using var clip = await cutter.CutAsync(
            source,
            positionSeconds,
            options.ClipSeconds,
            ct);

        var lease = await usage.BeginSttAsync(ct);

        SttResult? transcription = null;
        var resultCategory = "ProviderError";

        try
        {
            ct.ThrowIfCancellationRequested();

            transcription = await transcriber.TranscribeAsync(
                clip.Audio,
                "alignment.wav",
                "audio/wav",
                string.IsNullOrWhiteSpace(source.Metadata.Language)
                    ? null
                    : source.Metadata.Language.Trim(),
                ct);

            resultCategory = "Completed";
        }
        catch (OperationCanceledException)
        {
            resultCategory = "Cancelled";
            throw;
        }
        finally
        {
            await usage.CompleteSttAsync(
                lease,
                new TranscriptionUsage(
                    1,
                    resultCategory == "Completed"
                        ? transcription?.DurationSeconds
                        : null,
                    resultCategory),
                CancellationToken.None);
        }

        var match = matcher.Locate(
            transcription!.Text,
            new MatcherSettings());

        if (!match.Accepted)
            return AlignmentLocateResult.Declined(match.Reason);

        if (match.End?.Locator is not EpubBookTextSourceLocator end)
        {
            throw new AlignmentException(
                "alignment_locator_invalid",
                422,
                "The matched passage has no EPUB end locator.");
        }

        var locator = new AlignmentLocator(
            end.SpineIndex,
            end.ResourceHref,
            end.Cfi,
            end.EndTextOffset);

        var anchorStored = false;

        // Persistence is best-effort. A failed revision lookup or database
        // operation cannot turn an accepted alignment into an HTTP failure.
        if (snapshot.Revision is not null)
        {
            try
            {
                var audioRevision = await audioRevisions.GetAsync(
                    source, ct);

                if (audioRevision is not null &&
                    double.IsFinite(match.Score) &&
                    positionSeconds <= long.MaxValue / 1000d)
                {
                    var positionMs = checked(
                        (long)Math.Round(
                            positionSeconds * 1000,
                            MidpointRounding.AwayFromZero));

                    anchorStored = await anchors.SaveAsync(
                        new AlignmentAnchorInput(
                            source.Id,
                            target.Id,
                            audioRevision,
                            snapshot.Revision,
                            positionMs,
                            match.End.GlobalTextOffset,
                            locator.SpineIndex,
                            locator.ResourceHref,
                            locator.Cfi,
                            locator.TextOffset,
                            match.Score),
                        ct);
                }
            }
            catch (Exception ex)
            {
                // Include identifiers, but no transcription text,
                // secrets, storage paths or provider credentials.
                logger.LogWarning(
                    "Alignment anchor persistence failed for " +
                    "audio book {AudioBookId} and text book {TextBookId}: {ErrorType}",
                    source.Id,
                    target.Id,
                    ex.GetType().Name);
            }
        }

        return new AlignmentLocateResult(
            Located: true,
            Reason: null,
            Score: match.Score,
            Excerpt: match.Passage,
            Locator: locator,
            AnchorStored: anchorStored);
    }
}
