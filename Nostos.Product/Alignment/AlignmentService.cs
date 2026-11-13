using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;

namespace Nostos.Product.Alignment;

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
    private readonly AlignmentProbeReader probeReader = new();
    private readonly HedgedSttTranscriber sttTranscriber = new(
        transcriber, usage, options, logger);

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

        if (!await sttTranscriber.IsAvailableAsync(ct))
        {
            throw new AlignmentException(
                "alignment_transcription_unavailable",
                503,
                "Alignment transcription is not configured.");
        }

        var snapshot = await ReadDocumentAsync(target, ct);
        var matcher = new PassageMatcher(
            snapshot.Document,
            snapshot.ReadingAids);

        await using var clip = await cutter.CutAsync(
            source,
            positionSeconds,
            options.ClipSeconds,
            ct);

        var transcription = await sttTranscriber.TranscribeAsync(
            source, clip, ct);

        var match = matcher.Locate(transcription.Text);

        // A transcript often ends in words nobody said: the model fills a
        // pause, or the cut falls mid-word. The words just before them are
        // still the listener's place, a second or two earlier.
        var trimmed = false;

        if (!match.Accepted &&
            match.Reason is "weak_match" or "no_match")
        {
            var spoken = AlignmentTokens.Split(transcription.Text);
            var settings = MatcherSettings.For(spoken);
            var drop = settings.MaximumTokens / 4;

            if (spoken.Count >= settings.MaximumTokens + drop)
            {
                var earlier = matcher.LocateTokens(
                    spoken.Skip(spoken.Count - drop - settings.MaximumTokens)
                        .Take(settings.MaximumTokens),
                    settings);

                if (earlier.Accepted)
                {
                    match = earlier;
                    trimmed = true;
                }
            }
        }

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

                // An anchor pairs this exact second with this exact text. A
                // match found a few words back is the right place to open the
                // book, but not that pair.
                if (audioRevision is not null &&
                    !trimmed &&
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
        var matcher = new PassageMatcher(
            snapshot.Document,
            snapshot.ReadingAids);

        var excerptMatch = matcher.LocateOpening(excerpt);

        if (!excerptMatch.Accepted)
        {
            return AlignmentLocateAudioResult.Declined(
                excerptMatch.Reason == "ambiguous"
                    ? "excerpt_ambiguous"
                    : "excerpt_not_found");
        }

        // Anchors are stored and looked up by text offset. The search itself
        // counts spoken characters: a narrator's seconds are proportional to
        // the words read aloud, not to the running heads, glosses, punctuation
        // and spacing between them.
        var targetOffset = excerptMatch.Start!.GlobalTextOffset;
        var targetSpoken = excerptMatch.StartSpoken;
        var totalSpoken = matcher.TotalSpoken;

        var duration = AlignmentAudioDuration.GetSeconds(target);

        if (duration is null ||
            !double.IsFinite(duration.Value) ||
            duration.Value <= 0)
        {
            return AlignmentLocateAudioResult.Declined(
                "duration_unknown");
        }

        if (!await sttTranscriber.IsAvailableAsync(ct))
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

                    before = ToSearchPoint(atOrBefore, duration.Value, matcher);
                    after = ToSearchPoint(atOrAfter, duration.Value, matcher);
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
        var searchOptions = options.CloneNormalizedForSearch();

        AudioAlignmentSearch search;

        try
        {
            search = new AudioAlignmentSearch(
                targetSpoken,
                totalSpoken,
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

        // An anchor within a clip of the target already says where it is: a
        // reader switching again at the place they switched before, or at a
        // page located a moment ago, is answered without a transcription.
        if (search.AnchoredPositionSeconds is { } anchored)
        {
            return new AlignmentLocateAudioResult(
                Located: true,
                Reason: null,
                PositionSeconds: anchored,
                ApproximatePositionSeconds: null,
                Probes: 0,
                Score: 0,
                AnchorStored: false);
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
            var transcription = await sttTranscriber.TranscribeAsync(
                target, clip, ct);

            probes++;

            var reading = probeReader.ReadProbe(
                matcher,
                transcription.Text,
                clipStart,
                clipEnd,
                searchOptions.ProbeSeconds);

            if (reading is null)
            {
                search.Decline();
                continue;
            }

            var observation = new AudioAlignmentSearch.Observation(
                clipStart,
                clipEnd,
                (int)reading.Knots[0].TextOffset,
                (int)reading.Knots[^1].TextOffset,
                reading.Score,
                reading.Knots);

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
                    !AlignmentProbeReader.ConsistentWithAnchors(
                        locatedSeconds,
                        targetSpoken,
                        before,
                        after,
                        searchOptions.ProbeSeconds))
                {
                    return AlignmentLocateAudioResult.Declined(
                        "inconsistent_probes",
                        probes);
                }
            }

            // Persist only the pairs the transcript pins to the clip's own
            // ends: the first words it placed at the clip's start, the last at
            // its end. Later searches interpolate between anchors, so a pair
            // read off the middle of a clip is not kept.
            if (audioRevision is not null &&
                snapshot.Revision is not null)
            {
                if (reading.OpeningStart is not null)
                {
                    anyAnchorStored |= await TryStoreAnchorAsync(
                        target,
                        source,
                        audioRevision,
                        snapshot.Revision,
                        clipStart,
                        reading.OpeningStart.GlobalTextOffset,
                        ToAnchorLocator(reading.OpeningStart.Locator),
                        reading.Score,
                        ct);
                }

                if (reading.ClosingEnd is not null)
                {
                    anyAnchorStored |= await TryStoreAnchorAsync(
                        target,
                        source,
                        audioRevision,
                        snapshot.Revision,
                        clipEnd,
                        reading.ClosingEnd.GlobalTextOffset,
                        ToAnchorLocator(reading.ClosingEnd.Locator),
                        reading.Score,
                        ct);
                }
            }

            if (decision.Located)
            {
                return new AlignmentLocateAudioResult(
                    Located: true,
                    Reason: null,
                    PositionSeconds: decision.PositionSeconds,
                    ApproximatePositionSeconds: null,
                    Probes: probes,
                    Score: reading.Score,
                    AnchorStored: anyAnchorStored);
            }
        }

        // The search stopped without the target inside a probed clip, but a
        // probe ended within a clip or so of it. Counting on from that exact
        // pair at the book's pace is a few seconds of extrapolation, which is
        // a better answer than asking the reader to accept "roughly".
        if (search.NearTargetPositionSeconds is { } near &&
            AlignmentProbeReader.ConsistentWithAnchors(
                near,
                targetSpoken,
                before,
                after,
                searchOptions.ProbeSeconds * 2))
        {
            return new AlignmentLocateAudioResult(
                Located: true,
                Reason: null,
                PositionSeconds: near,
                ApproximatePositionSeconds: null,
                Probes: probes,
                Score: 0,
                AnchorStored: anyAnchorStored);
        }

        return AlignmentLocateAudioResult.Declined(
            "probe_budget_exhausted",
            probes,
            search.ApproximatePositionSeconds);
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
        double duration,
        PassageMatcher matcher)
    {
        if (anchor is null)
            return null;

        var seconds = anchor.AudioPositionMs / 1000d;

        return seconds >= 0 && seconds <= duration
            ? new AudioAlignmentSearch.Point(
                seconds,
                matcher.SpokenOffset(anchor.TextGlobalOffset))
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
