using System.Text.RegularExpressions;
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
    public double ProbeSeconds { get; set; } = 20;
    public int MaxProbes { get; set; } = 5;
    public int MatchToleranceCharacters { get; set; } = 40;
    public int ConsistencyToleranceCharacters { get; set; } = 40;
    public double LeadInSeconds { get; set; } = 1.0;
    public double MaximumLocalRateRatio { get; set; } = 5;

    // Historical configuration name. Applies to both EPUB and PDF.
    // Keep the existing Alignment:MaxEpubBytes binding.
    public long MaxEpubBytes { get; set; } =
        200L * 1024 * 1024;

    public long MaxCachedCharacters { get; set; } =
        20_000_000;

    /// <summary>
    /// Limits the cost and size of reverse-alignment probes, including
    /// when options are constructed outside the normal DI registration.
    /// </summary>
    public void NormalizeProbeSettings()
    {
        MaxProbes = Math.Clamp(MaxProbes, 1, 8);

        ProbeSeconds = double.IsNaN(ProbeSeconds)
            ? 20
            : Math.Clamp(ProbeSeconds, 5, 30);
    }
}

public sealed record AlignmentLocator(
    int SpineIndex,
    string ResourceHref,
    string? Cfi,
    int? TextOffset);

public sealed record AlignmentPdfLocator(
    int PageIndex,
    int PageNumber,
    string? PageLabel,
    int TextOffset);

public sealed record AlignmentLocateResult(
    bool Located,
    string? Reason,
    double? Score,
    string? Excerpt,
    AlignmentLocator? Locator,
    bool AnchorStored = false,
    AlignmentPdfLocator? Pdf = null,
    string? SourceText = null)
{
    public static AlignmentLocateResult Declined(string reason) =>
        new(false, reason, null, null, null);
}

public sealed record AlignmentLocateAudioResult(
    bool Located,
    string? Reason,
    double? PositionSeconds,
    double? ApproximatePositionSeconds,
    int Probes,
    double? Score,
    bool AnchorStored)
{
    public static AlignmentLocateAudioResult Declined(
        string reason,
        int probes = 0,
        double? approximatePositionSeconds = null) =>
        new(
            false,
            reason,
            null,
            approximatePositionSeconds,
            probes,
            null,
            false);
}

public interface IAlignmentService
{
    Task<AlignmentLocateResult> LocateAsync(
        AudioBookModel source,
        EBookModel target,
        double positionSeconds,
        CancellationToken ct = default);

    Task<AlignmentLocateAudioResult> LocateAudioAsync(
        EBookModel source,
        AudioBookModel target,
        string excerpt,
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
    private static readonly Regex Words = new(
        @"[\p{L}\p{N}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        ValidatePair(source, target);

        if (!await transcriber.IsAvailableAsync(ct))
        {
            throw new AlignmentException(
                "alignment_transcription_unavailable",
                503,
                "Alignment transcription is not configured.");
        }

        var snapshot = await ReadDocumentAsync(target, ct);
        var matcher = new PassageMatcher(snapshot.Document);

        await using var clip = await cutter.CutAsync(
            source,
            positionSeconds,
            options.ClipSeconds,
            ct);

        var transcription = await TranscribeAsync(
            source, clip, ct);

        var match = matcher.Locate(
            transcription.Text,
            new MatcherSettings());

        if (!match.Accepted)
            return AlignmentLocateResult.Declined(match.Reason);

        if (match.Start is null ||
            match.End?.Locator is not
                (EpubBookTextSourceLocator or PdfBookTextSourceLocator))
        {
            throw new AlignmentException(
                "alignment_locator_invalid",
                422,
                "The matched passage has no supported end locator.");
        }

        var end = match.End.Locator;
        var locator = end is EpubBookTextSourceLocator epub
            ? ToLocator(epub)
            : null;

        var pdf = end is PdfBookTextSourceLocator pdfEnd
            ? new AlignmentPdfLocator(
                pdfEnd.PageIndex,
                checked(pdfEnd.PageIndex + 1),
                pdfEnd.PageLabel,
                pdfEnd.EndTextOffset ?? 0)
            : null;

        var sourceText = MatchedSourceText(
            snapshot.Document,
            match.Start.GlobalTextOffset,
            match.End.GlobalTextOffset);

        var anchorLocator = ToAnchorLocator(end);
        var anchorStored = false;

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
                            anchorLocator.SpineIndex,
                            anchorLocator.ResourceHref,
                            anchorLocator.Cfi,
                            anchorLocator.TextOffset,
                            match.Score),
                        ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogAnchorFailure(source.Id, target.Id, ex);
            }
        }

        return new AlignmentLocateResult(
            Located: true,
            Reason: null,
            Score: match.Score,
            Excerpt: match.Passage,
            Locator: locator,
            AnchorStored: anchorStored,
            Pdf: pdf,
            SourceText: sourceText);
    }

    public async Task<AlignmentLocateAudioResult> LocateAudioAsync(
        EBookModel source,
        AudioBookModel target,
        string excerpt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(excerpt) ||
            excerpt.Length > 2000)
        {
            throw new AlignmentException(
                "alignment_invalid_excerpt",
                400,
                "An excerpt of at most 2000 characters is required.");
        }

        ValidatePair(target, source);

        // Resolve the text before spending any transcription allowance.
        var snapshot = await ReadDocumentAsync(source, ct);
        var matcher = new PassageMatcher(snapshot.Document);

        var excerptMatch = matcher.Locate(
            FirstMatcherWindow(excerpt),
            new MatcherSettings());

        if (!excerptMatch.Accepted)
        {
            return AlignmentLocateAudioResult.Declined(
                excerptMatch.Reason == "ambiguous"
                    ? "excerpt_ambiguous"
                    : "excerpt_not_found");
        }

        var targetOffset = excerptMatch.Start!.GlobalTextOffset;
        var totalCharacters = GlobalTextLength(snapshot.Document);

        var duration = AlignmentAudioDuration.GetSeconds(target);

        if (duration is null ||
            !double.IsFinite(duration.Value) ||
            duration.Value <= 0)
        {
            return AlignmentLocateAudioResult.Declined(
                "duration_unknown");
        }

        if (!await transcriber.IsAvailableAsync(ct))
        {
            throw new AlignmentException(
                "alignment_transcription_unavailable",
                503,
                "Alignment transcription is not configured.");
        }

        string? audioRevision = null;
        AudioAlignmentSearch.Point? before = null;
        AudioAlignmentSearch.Point? after = null;

        if (snapshot.Revision is not null)
        {
            try
            {
                audioRevision = await audioRevisions.GetAsync(
                    target, ct);

                if (audioRevision is not null)
                {
                    var atOrBefore = await anchors.AtOrBeforeTextAsync(
                        target.Id,
                        source.Id,
                        audioRevision,
                        snapshot.Revision,
                        targetOffset,
                        ct);

                    var atOrAfter = await anchors.AtOrAfterTextAsync(
                        target.Id,
                        source.Id,
                        audioRevision,
                        snapshot.Revision,
                        targetOffset,
                        ct);

                    before = ToSearchPoint(atOrBefore, duration.Value);
                    after = ToSearchPoint(atOrAfter, duration.Value);
                }
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException ||
                !ct.IsCancellationRequested)
            {
                LogAnchorFailure(target.Id, source.Id, ex);
                audioRevision = null;
                before = null;
                after = null;
            }
        }

        // The registered options are normalized during composition.
        // Use a local bounded copy so independently constructed services
        // cannot spend more than eight transcription calls.
        var searchOptions = new AlignmentOptions
        {
            ClipSeconds = options.ClipSeconds,
            ProbeSeconds = options.ProbeSeconds,
            MaxProbes = options.MaxProbes,
            MatchToleranceCharacters = options.MatchToleranceCharacters,
            ConsistencyToleranceCharacters =
                options.ConsistencyToleranceCharacters,
            LeadInSeconds = options.LeadInSeconds,
            MaximumLocalRateRatio = options.MaximumLocalRateRatio,
            MaxEpubBytes = options.MaxEpubBytes,
            MaxCachedCharacters = options.MaxCachedCharacters,
        };

        searchOptions.NormalizeProbeSettings();

        if (!double.IsFinite(searchOptions.LeadInSeconds) ||
            searchOptions.LeadInSeconds < 0)
            searchOptions.LeadInSeconds = 0;

        searchOptions.LeadInSeconds = Math.Min(
            searchOptions.LeadInSeconds,
            searchOptions.ProbeSeconds);

        AudioAlignmentSearch search;

        try
        {
            search = new AudioAlignmentSearch(
                targetOffset,
                totalCharacters,
                duration.Value,
                searchOptions,
                before,
                after);
        }
        catch (ArgumentOutOfRangeException)
        {
            return AlignmentLocateAudioResult.Declined(
                "duration_unknown");
        }

        var probes = 0;
        var anyAnchorStored = false;

        for (var attempt = 0;
             attempt < searchOptions.MaxProbes && search.HasNextProbe;
             attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var clipEnd = search.ClipEndSeconds;
            var clipStart = search.ClipStartSeconds;

            if (!double.IsFinite(clipStart) ||
                !double.IsFinite(clipEnd) ||
                clipStart < 0 ||
                clipEnd <= clipStart ||
                clipEnd > duration.Value)
            {
                return AlignmentLocateAudioResult.Declined(
                    "duration_unknown",
                    probes);
            }

            await using var clip = await cutter.CutAsync(
                target,
                clipEnd,
                searchOptions.ProbeSeconds,
                ct);

            // BeginSttAsync is invoked once per transcription, not once
            // per search. A denied lease propagates without retrying.
            var transcription = await TranscribeAsync(
                target, clip, ct);

            probes++;

            var match = matcher.Locate(
                transcription.Text,
                new MatcherSettings());

            if (!match.Accepted)
            {
                search.Decline();
                continue;
            }

            if (match.Start?.Locator is not
                    (EpubBookTextSourceLocator or PdfBookTextSourceLocator) ||
                match.End?.Locator is not
                    (EpubBookTextSourceLocator or PdfBookTextSourceLocator))
            {
                throw new AlignmentException(
                    "alignment_locator_invalid",
                    422,
                    "A probe has no supported text source provenance.");
            }

            var start = match.Start.Locator;
            var end = match.End.Locator;

            var observation = new AudioAlignmentSearch.Observation(
                clipStart,
                clipEnd,
                match.Start.GlobalTextOffset,
                match.End.GlobalTextOffset,
                match.Score);

            var decision = search.Observe(observation);

            if (decision.Inconsistent)
            {
                return AlignmentLocateAudioResult.Declined(
                    "inconsistent_probes",
                    probes);
            }

            // The neighbouring anchors belong to the exact audio and
            // text-file revisions. A probe that violates their ordering
            // cannot establish a trustworthy timestamp.
            //
            // Check before saving the probe's derived anchors, so the
            // current measurement cannot change its own guardrails.
            if (decision.Located &&
                decision.PositionSeconds is double locatedSeconds)
            {
                if (!double.IsFinite(locatedSeconds) ||
                    locatedSeconds < 0 ||
                    locatedSeconds > duration.Value ||
                    !ConsistentWithAnchors(
                        locatedSeconds,
                        targetOffset,
                        before,
                        after,
                        searchOptions.ProbeSeconds))
                {
                    return AlignmentLocateAudioResult.Declined(
                        "inconsistent_probes",
                        probes);
                }
            }

            // Persist the two approximate clip boundaries. Each is
            // revision-bound and uses its own text-source locator.
            if (audioRevision is not null &&
                snapshot.Revision is not null)
            {
                anyAnchorStored |= await TryStoreAnchorAsync(
                    target,
                    source,
                    audioRevision,
                    snapshot.Revision,
                    clipStart,
                    match.Start.GlobalTextOffset,
                    ToAnchorLocator(start),
                    match.Score,
                    ct);

                anyAnchorStored |= await TryStoreAnchorAsync(
                    target,
                    source,
                    audioRevision,
                    snapshot.Revision,
                    clipEnd,
                    match.End.GlobalTextOffset,
                    ToAnchorLocator(end),
                    match.Score,
                    ct);
            }

            if (decision.Located)
            {
                return new AlignmentLocateAudioResult(
                    Located: true,
                    Reason: null,
                    PositionSeconds: decision.PositionSeconds,
                    ApproximatePositionSeconds: null,
                    Probes: probes,
                    Score: match.Score,
                    AnchorStored: anyAnchorStored);
            }
        }

        return AlignmentLocateAudioResult.Declined(
            "probe_budget_exhausted",
            probes,
            search.ApproximatePositionSeconds);
    }

    private static bool ConsistentWithAnchors(
        double positionSeconds,
        int targetOffset,
        AudioAlignmentSearch.Point? before,
        AudioAlignmentSearch.Point? after,
        double toleranceSeconds)
    {
        if (before is null || after is null ||
            before.TextOffset > targetOffset ||
            after.TextOffset < targetOffset)
            return true;

        if (!double.IsFinite(toleranceSeconds) ||
            toleranceSeconds <= 0)
            return false;

        if (before.AudioSeconds > after.AudioSeconds)
            return false;

        var low = before.AudioSeconds - toleranceSeconds;
        var high = after.AudioSeconds + toleranceSeconds;

        return double.IsFinite(positionSeconds) &&
            positionSeconds >= low &&
            positionSeconds <= high;
    }

    private async Task<AlignmentDocumentSnapshot> ReadDocumentAsync(
        EBookModel book,
        CancellationToken ct)
    {
        if (!BookTextFormatResolver.TryResolve(
                book.FileDetails.FileName, out var format) ||
            format is not
                (BookTextSourceFormat.Epub or BookTextSourceFormat.Pdf))
        {
            throw new AlignmentException(
                "alignment_target_not_epub",
                422,
                "The text edition must be an EPUB or PDF with stored media.");
        }

        var extractor = extractors.Single(x => x.Format == format);

        return await cache.GetAsync(
            book.Id, storage, extractor, ct);
    }

    private async Task<SttResult> TranscribeAsync(
        AudioBookModel book,
        AudioClip clip,
        CancellationToken ct)
    {
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
                string.IsNullOrWhiteSpace(book.Metadata.Language)
                    ? null
                    : book.Metadata.Language.Trim(),
                ct);

            resultCategory = "Completed";
            return transcription;
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
    }

    private async Task<bool> TryStoreAnchorAsync(
        AudioBookModel audio,
        EBookModel text,
        string audioRevision,
        string textRevision,
        double time,
        int offset,
        AlignmentLocator locator,
        double score,
        CancellationToken ct)
    {
        if (!double.IsFinite(time) ||
            time < 0 ||
            time > long.MaxValue / 1000d ||
            !double.IsFinite(score))
            return false;

        try
        {
            return await anchors.SaveAsync(
                new AlignmentAnchorInput(
                    audio.Id,
                    text.Id,
                    audioRevision,
                    textRevision,
                    checked((long)Math.Round(
                        time * 1000,
                        MidpointRounding.AwayFromZero)),
                    offset,
                    locator.SpineIndex,
                    locator.ResourceHref,
                    locator.Cfi,
                    locator.TextOffset,
                    score),
                ct);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException ||
            !ct.IsCancellationRequested)
        {
            LogAnchorFailure(audio.Id, text.Id, ex);
            return false;
        }
    }

    private static AudioAlignmentSearch.Point? ToSearchPoint(
        AlignmentAnchorModel? anchor,
        double duration)
    {
        if (anchor is null)
            return null;

        var seconds = anchor.AudioPositionMs / 1000d;

        return seconds >= 0 && seconds <= duration
            ? new AudioAlignmentSearch.Point(
                seconds, anchor.TextGlobalOffset)
            : null;
    }

    private static AlignmentLocator ToLocator(
        EpubBookTextSourceLocator epub) =>
        new(
            epub.SpineIndex,
            epub.ResourceHref,
            epub.Cfi,
            epub.EndTextOffset);

    private static AlignmentLocator ToAnchorLocator(
        BookTextSourceLocator source) =>
        source switch
        {
            EpubBookTextSourceLocator epub => ToLocator(epub),

            // Anchor schema remains unchanged. For PDF:
            // SpineIndex = zero-based physical page index,
            // ResourceHref = fixed non-empty sentinel,
            // Cfi = null, TextOffset = normalized page offset.
            // Searches use only TextGlobalOffset and AudioPositionMs.
            PdfBookTextSourceLocator pdf => new AlignmentLocator(
                pdf.PageIndex,
                "pdf-page",
                null,
                pdf.EndTextOffset),

            _ => throw new AlignmentException(
                "alignment_locator_invalid",
                422,
                "The text source locator is unsupported."),
        };

    private static string MatchedSourceText(
        BookTextExtractedDocument document,
        int start,
        int end)
    {
        if (start < 0 || end <= start)
            return string.Empty;

        const int maximumCharacters = 300;

        // Global offsets are measured in the same coordinate system as
        // PassageMatcher: block text plus one virtual separator per block.
        // Keep the final characters so the returned text ends at the
        // matched passage end, even when a passage crosses blocks.
        var pieces = new List<string>();
        var global = 0;

        foreach (var block in document.Blocks)
        {
            var blockStart = global;
            var blockEnd = checked(global + block.Text.Length);

            var from = Math.Max(start, blockStart);
            var to = Math.Min(end, blockEnd);

            if (to > from)
            {
                pieces.Add(block.Text.Substring(
                    from - blockStart,
                    to - from));
            }

            global = checked(blockEnd + 1);

            if (global >= end)
                break;
        }

        var text = Regex.Replace(
            string.Join(" ", pieces),
            @"\s+",
            " ").Trim();

        return text.Length <= maximumCharacters
            ? text
            : text[^maximumCharacters..];
    }

    private static string FirstMatcherWindow(string excerpt)
    {
        // PassageMatcher uses TakeLast(MaximumTokens). Sending the whole
        // excerpt would match its end, rather than the first visible words.
        var words = Words.Matches(excerpt);

        const int maximumTokens = 24;

        if (words.Count <= maximumTokens)
            return excerpt.Trim();

        var last = words[maximumTokens - 1];

        return excerpt[..(last.Index + last.Length)].Trim();
    }

    private static int GlobalTextLength(
        BookTextExtractedDocument document)
    {
        long length = 0;

        foreach (var block in document.Blocks)
            length = checked(length + block.Text.Length + 1L);

        // PassageMatcher inserts one separator after each block.
        // There is no need for a final trailing separator.
        length = Math.Max(0, length - 1);

        if (length <= 0 || length > int.MaxValue)
        {
            throw new AlignmentException(
                "alignment_target_text_unavailable",
                422,
                "The text edition cannot be indexed for alignment.");
        }

        return (int)length;
    }

    private static void ValidatePair(
        AudioBookModel audio,
        EBookModel text)
    {
        if (!audio.FileDetails.HasFile)
        {
            throw new AlignmentException(
                "alignment_source_media_missing",
                422,
                "The audiobook has no stored audio.");
        }

        if (!text.FileDetails.HasFile ||
            !BookTextFormatResolver.TryResolve(
                text.FileDetails.FileName, out var format) ||
            format is not
                (BookTextSourceFormat.Epub or BookTextSourceFormat.Pdf))
        {
            // Retain the historical code for existing clients.
            throw new AlignmentException(
                "alignment_target_not_epub",
                422,
                "The ebook must be an EPUB or PDF with stored media.");
        }

        if (audio.WorkId == Guid.Empty ||
            audio.WorkId != text.WorkId)
        {
            throw new AlignmentException(
                "alignment_work_mismatch",
                422,
                "The two editions do not belong to the same work.");
        }
    }

    private void LogAnchorFailure(
        Guid audioBookId,
        Guid textBookId,
        Exception ex)
    {
        logger.LogWarning(
            "Alignment anchor operation failed for " +
            "audio book {AudioBookId} and text book {TextBookId}: {ErrorType}",
            audioBookId,
            textBookId,
            ex.GetType().Name);
    }
}
