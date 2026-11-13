using Microsoft.Extensions.Logging;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Product.Services.Ai;

namespace Nostos.Product.Alignment;

internal sealed class HedgedSttTranscriber(
    IAlignmentTranscriber transcriber,
    IAiUsageAccountingService usage,
    AlignmentOptions options,
    ILogger logger)
{
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        transcriber.IsAvailableAsync(ct);

    public Task<SttResult> TranscribeAsync(
        AudioBookModel book,
        AudioClip clip,
        CancellationToken ct = default) =>
        double.IsFinite(options.HedgeAfterSeconds) &&
        options.HedgeAfterSeconds > 0
            ? TranscribeHedgedAsync(book, clip, options.HedgeAfterSeconds, ct)
            : TranscribeOnceAsync(book, clip, ct);

    /// <summary>
    /// One clip, asked a second time when the first request is slow. Whichever
    /// answers first is used and the other is cancelled.
    ///
    /// Each request has its own usage lease. The leases are opened and settled
    /// here, one at a time: the usage service belongs to the request and is not
    /// used from two tasks at once. Only the calls to the provider overlap.
    /// </summary>
    private async Task<SttResult> TranscribeHedgedAsync(
        AudioBookModel book,
        AudioClip clip,
        double hedgeAfterSeconds,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await clip.Audio.CopyToAsync(buffer, ct);
        var audio = buffer.ToArray();

        var language = string.IsNullOrWhiteSpace(book.Metadata.Language)
            ? null
            : book.Metadata.Language.Trim();

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task<SttResult> AskAsync()
        {
            race.Token.ThrowIfCancellationRequested();

            return await transcriber.TranscribeAsync(
                new MemoryStream(audio, writable: false),
                "alignment.wav",
                "audio/wav",
                language,
                race.Token);
        }

        var firstLease = await usage.BeginSttAsync(ct);
        var first = AskAsync();

        AiUsageLease? secondLease = null;
        Task<SttResult>? second = null;

        try
        {
            var patience = Task.Delay(
                TimeSpan.FromSeconds(hedgeAfterSeconds), race.Token);

            if (await Task.WhenAny(first, patience) != first)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    secondLease = await usage.BeginSttAsync(ct);
                    second = AskAsync();

                    logger.LogInformation(
                        "Alignment transcription for audio book {AudioBookId} " +
                        "had not answered after {Seconds} s and was asked again.",
                        book.Id,
                        hedgeAfterSeconds);
                }
                catch (AiUsageException)
                {
                    // No allowance for a second request: the first one stands.
                }
            }

            if (second is null)
                return await first;

            var finished = await Task.WhenAny(first, second);

            if (finished.IsCompletedSuccessfully)
                return await finished;

            // The request that finished failed. The other may still answer;
            // if it does not, the first request's failure is the one reported.
            var other = finished == first ? second : first;

            try
            {
                return await other;
            }
            catch when (!first.IsCompletedSuccessfully)
            {
                return await first;
            }
        }
        finally
        {
            await race.CancelAsync();
            await SettleAsync(first, firstLease);

            if (second is not null)
                await SettleAsync(second, secondLease);
        }
    }

    private async Task SettleAsync(
        Task<SttResult> request,
        AiUsageLease? lease)
    {
        try
        {
            // A cancelled provider call ends promptly; never wait on one that
            // does not.
            await request.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        var category = request.IsCompletedSuccessfully
            ? "Completed"
            : request.IsFaulted && request.Exception?.InnerException
                is not OperationCanceledException
                ? "ProviderError"
                : "Cancelled";

        // Observe a request that outlives the wait above.
        _ = request.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        await usage.CompleteSttAsync(
            lease,
            new TranscriptionUsage(
                1,
                request.IsCompletedSuccessfully
                    ? request.Result.DurationSeconds
                    : null,
                category),
            CancellationToken.None);
    }

    private async Task<SttResult> TranscribeOnceAsync(
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
}
