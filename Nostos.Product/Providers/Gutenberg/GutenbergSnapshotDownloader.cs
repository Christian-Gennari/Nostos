using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// The resumable byte-range transfer for Project Gutenberg's daily catalogue
/// archive: one HTTP GET, then as many conditional <c>Range</c> continuations as
/// its retry budget allows, each checked against the validators the HEAD probe
/// saw.
///
/// The transfer owns the state machine and the retry policy. It knows nothing
/// about archives or ebooks: it produces one complete file on disk, or a
/// <see cref="ProviderException"/> explaining why it could not.
/// </summary>
internal sealed class GutenbergSnapshotDownloader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GutenbergSnapshotLimits _snapshotLimits;
    private readonly GutenbergSnapshotDownloadOptions _snapshotDownloadOptions;
    private readonly ILogger _logger;
    private readonly string _providerId;

    public GutenbergSnapshotDownloader(
        IHttpClientFactory httpClientFactory,
        GutenbergSnapshotLimits snapshotLimits,
        GutenbergSnapshotDownloadOptions snapshotDownloadOptions,
        ILogger logger,
        string providerId)
    {
        _httpClientFactory = httpClientFactory;
        _snapshotLimits = snapshotLimits;
        _snapshotDownloadOptions = snapshotDownloadOptions;
        _logger = logger;
        _providerId = providerId;
    }

    /// <summary>
    /// Downloads the snapshot to <paramref name="path"/>, resuming from whatever
    /// complete prefix is already there when the source allows it.
    ///
    /// A declared length over the archive cap is rejected before any transfer
    /// starts; a body that outruns the declared length or the cap is a corrupt
    /// response. Exhausting the retry budget, the wall-clock deadline or the
    /// no-progress allowance is an unavailable source.
    /// </summary>
    public async Task DownloadAsync(
        string path,
        long? probedContentLength,
        GutenbergSnapshotValidator probedValidator,
        CancellationToken ct)
    {
        if (probedContentLength > _snapshotLimits.MaxSnapshotBytes)
        {
            throw ProviderException.InvalidResponse(
                _providerId,
                $"the catalogue snapshot declares {probedContentLength} bytes, over the {_snapshotLimits.MaxSnapshotBytes} byte limit");
        }

        using var deadline = new CancellationTokenSource(_snapshotDownloadOptions.MaxDuration);
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var stopwatch = Stopwatch.StartNew();
        GutenbergSnapshotTransfer? transfer = null;
        Exception? lastFailure = null;
        TimeSpan? retryAfter = null;
        var attemptsMade = 0;
        var consecutiveNoProgressAttempts = 0;

        for (var attempt = 1; attempt <= _snapshotDownloadOptions.MaxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();

            var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
            var useRange = offset > 0
                && transfer is { CanResume: true, TotalLength: not null }
                && offset < transfer.TotalLength.Value
                && transfer.Validator.TryCreateIfRange(out _);

            // A partial body without a range-safe validator cannot be appended
            // safely. Start the next attempt with an empty file instead.
            if (offset > 0 && !useRange)
            {
                File.Delete(path);
                offset = 0;
                transfer = null;
            }

            GutenbergSnapshotDownloadAttempt result;
            try
            {
                attemptsMade++;
                result = await DownloadAttemptAsync(
                    path,
                    offset,
                    useRange,
                    transfer,
                    probedContentLength,
                    probedValidator,
                    ct,
                    downloadCancellation.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }

            if (result.Completed)
                return;

            if (result.RestartFromZero)
            {
                if (File.Exists(path))
                    File.Delete(path);
                transfer = null;
            }
            else
            {
                transfer = result.Transfer;
            }

            lastFailure = result.Error;
            retryAfter = result.RetryAfter;

            if (result.BytesReceived >= _snapshotDownloadOptions.MinimumProgressBytes)
                consecutiveNoProgressAttempts = 0;
            else
                consecutiveNoProgressAttempts++;

            var bytesSoFar = File.Exists(path) ? new FileInfo(path).Length : 0;
            var expectedBytes = result.Transfer?.TotalLength ?? probedContentLength;
            var remaining = _snapshotDownloadOptions.MaxDuration - stopwatch.Elapsed;
            var delay = RetryDelay(consecutiveNoProgressAttempts, retryAfter);
            var canRetry = attempt < _snapshotDownloadOptions.MaxAttempts
                && consecutiveNoProgressAttempts < _snapshotDownloadOptions.MaxConsecutiveNoProgressAttempts
                && remaining > TimeSpan.Zero
                && delay <= remaining;

            _logger.LogInformation(
                "Gutenberg snapshot download attempt {Attempt}/{MaxAttempts} ended after {BytesAdvanced:N0} new bytes; {BytesSoFar:N0} of {ExpectedBytes} bytes received; {ConsecutiveNoProgressAttempts}/{MaxConsecutiveNoProgressAttempts} consecutive attempts were below {MinimumProgressBytes:N0} bytes. {RetryAction}.",
                attempt,
                _snapshotDownloadOptions.MaxAttempts,
                result.BytesReceived,
                bytesSoFar,
                expectedBytes?.ToString("N0") ?? "unknown",
                consecutiveNoProgressAttempts,
                _snapshotDownloadOptions.MaxConsecutiveNoProgressAttempts,
                _snapshotDownloadOptions.MinimumProgressBytes,
                canRetry ? "Retrying" : "Stopping");

            // A Retry-After longer than this transfer's remaining budget cannot
            // be honoured without violating the overall wall-clock bound.
            if (!canRetry)
                break;

            try
            {
                await Task.Delay(delay, downloadCancellation.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
        }

        if (ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();

        var reason = deadline.IsCancellationRequested || stopwatch.Elapsed >= _snapshotDownloadOptions.MaxDuration
            ? "the download exceeded its time limit"
            : consecutiveNoProgressAttempts >= _snapshotDownloadOptions.MaxConsecutiveNoProgressAttempts
                ? $"the download made fewer than {_snapshotDownloadOptions.MinimumProgressBytes} new bytes in {consecutiveNoProgressAttempts} consecutive attempts"
                : $"the download failed after {attemptsMade} attempts";
        if (lastFailure is not null)
            reason += $": {lastFailure.Message}";

        throw ProviderException.UnavailableFor(_providerId, reason);
    }

    private async Task<GutenbergSnapshotDownloadAttempt> DownloadAttemptAsync(
        string path,
        long offset,
        bool useRange,
        GutenbergSnapshotTransfer? previousTransfer,
        long? probedContentLength,
        GutenbergSnapshotValidator probedValidator,
        CancellationToken callerToken,
        CancellationToken downloadToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GutenbergCatalog.SnapshotPath);
        if (useRange && previousTransfer is { TotalLength: not null }
            && previousTransfer.Validator.TryCreateIfRange(out var ifRange))
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = ifRange;
        }

        HttpResponseMessage response;
        try
        {
            var snapshotClient = ProviderHttp.Client(
                _httpClientFactory,
                GutenbergProvider.SnapshotHttpClientName);
            response = await snapshotClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                downloadToken);
        }
        catch (OperationCanceledException) when (!downloadToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            return GutenbergSnapshotDownloadAttempt.Retry(
                previousTransfer,
                null,
                new TimeoutException("the snapshot request timed out"));
        }
        catch (HttpRequestException ex)
        {
            return GutenbergSnapshotDownloadAttempt.Retry(previousTransfer, null, ex);
        }
        catch (IOException ex)
        {
            return GutenbergSnapshotDownloadAttempt.Retry(previousTransfer, null, ex);
        }

        using (response)
        {
            if (IsTransientSnapshotStatus(response.StatusCode))
            {
                return GutenbergSnapshotDownloadAttempt.Retry(
                    previousTransfer,
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                        ? GetRetryAfter(response)
                        : null,
                    new HttpRequestException("the catalogue snapshot answered HTTP " + (int)response.StatusCode));
            }

            if (useRange)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // If-Range failed or the server ignored Range. This is a
                    // complete representation, so truncate the partial file
                    // and use this response from byte zero after checking it.
                    if (!TryGetFullTransfer(
                            response,
                            probedContentLength,
                            probedValidator,
                            out var fullTransfer,
                            out var fullLength,
                            out var fullError))
                    {
                        return GutenbergSnapshotDownloadAttempt.Restart(fullError);
                    }

                    return await CopyResponseAsync(
                        path,
                        response,
                        offset: 0,
                        append: false,
                        fullTransfer,
                        fullLength,
                        downloadToken);
                }

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (!TryGetPartialTransfer(
                            response,
                            offset,
                            previousTransfer!,
                            probedContentLength,
                            probedValidator,
                            out var partialTransfer,
                            out var segmentLength,
                            out var partialError))
                    {
                        return GutenbergSnapshotDownloadAttempt.Restart(partialError);
                    }

                    return await CopyResponseAsync(
                        path,
                        response,
                        offset,
                        append: true,
                        partialTransfer,
                        segmentLength,
                        downloadToken);
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    return GutenbergSnapshotDownloadAttempt.Restart(
                        new IOException("the snapshot rejected the requested byte range"));

                throw ProviderException.UnavailableFor(
                    _providerId,
                    "the catalogue snapshot answered HTTP " + (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.PartialContent)
                return GutenbergSnapshotDownloadAttempt.Restart(
                    new IOException("the snapshot returned a partial response without a range request"));

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw ProviderException.UnavailableFor(
                    _providerId,
                    "the catalogue snapshot answered HTTP " + (int)response.StatusCode);
            }

            if (!TryGetFullTransfer(
                    response,
                    probedContentLength,
                    probedValidator,
                    out var transfer,
                    out var contentLength,
                    out var error))
            {
                return GutenbergSnapshotDownloadAttempt.Restart(error);
            }

            return await CopyResponseAsync(
                path,
                response,
                offset: 0,
                append: false,
                transfer,
                contentLength,
                downloadToken);
        }
    }

    private async Task<GutenbergSnapshotDownloadAttempt> CopyResponseAsync(
        string path,
        HttpResponseMessage response,
        long offset,
        bool append,
        GutenbergSnapshotTransfer transfer,
        long? expectedSegmentLength,
        CancellationToken ct)
    {
        long written = 0;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(
                path,
                append ? FileMode.Open : FileMode.Create,
                append ? FileAccess.ReadWrite : FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (append)
            {
                if (destination.Length != offset)
                    return GutenbergSnapshotDownloadAttempt.Restart(
                        new IOException("the partial snapshot file changed before resume"));
                destination.Position = offset;
            }

            var buffer = new byte[64 * 1024];
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

            while (true)
            {
                readTimeout.CancelAfter(_snapshotDownloadOptions.ReadTimeout);
                var read = await source.ReadAsync(buffer.AsMemory(), readTimeout.Token);
                if (read == 0)
                    break;

                if (expectedSegmentLength is { } segmentLimit && written + read > segmentLimit)
                {
                    throw ProviderException.InvalidResponse(
                        _providerId,
                        "the snapshot response exceeded its declared range length");
                }

                if (offset + written + read > _snapshotLimits.MaxSnapshotBytes)
                {
                    throw ProviderException.InvalidResponse(
                        _providerId,
                        $"the catalogue snapshot passed the {_snapshotLimits.MaxSnapshotBytes} byte limit");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
            }

            await destination.FlushAsync(ct);

            if (expectedSegmentLength is { } declaredSegment && written < declaredSegment)
            {
                return GutenbergSnapshotDownloadAttempt.Retry(
                    transfer,
                    null,
                    new EndOfStreamException(
                        $"the snapshot response ended after {written} of {declaredSegment} declared bytes"),
                    written);
            }

            var finalLength = offset + written;
            if (transfer.TotalLength is not { } totalLength)
            {
                throw ProviderException.InvalidResponse(
                    _providerId,
                    "the catalogue snapshot has no declared content length");
            }

            if (finalLength > totalLength)
                throw ProviderException.InvalidResponse(
                    _providerId,
                    "the snapshot exceeded its declared content length");

            if (finalLength < totalLength)
            {
                return GutenbergSnapshotDownloadAttempt.Retry(
                    transfer,
                    null,
                    new EndOfStreamException(
                        $"the snapshot ended at {finalLength} of {totalLength} declared bytes"),
                    written);
            }

            return GutenbergSnapshotDownloadAttempt.Success(transfer, written);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return GutenbergSnapshotDownloadAttempt.Retry(
                transfer,
                null,
                new TimeoutException("the snapshot stream read timed out"),
                written);
        }
        catch (HttpRequestException ex)
        {
            return GutenbergSnapshotDownloadAttempt.Retry(transfer, null, ex, written);
        }
        catch (IOException ex)
        {
            return GutenbergSnapshotDownloadAttempt.Retry(transfer, null, ex, written);
        }
    }

    private bool TryGetFullTransfer(
        HttpResponseMessage response,
        long? probedContentLength,
        GutenbergSnapshotValidator probedValidator,
        out GutenbergSnapshotTransfer transfer,
        out long? expectedLength,
        out Exception error)
    {
        var responseLength = response.Content.Headers.ContentLength;
        expectedLength = responseLength ?? probedContentLength;
        var responseValidator = GutenbergSnapshotValidator.FromResponse(response);

        if (expectedLength > _snapshotLimits.MaxSnapshotBytes)
        {
            throw ProviderException.InvalidResponse(
                _providerId,
                $"the catalogue snapshot declares {expectedLength} bytes, over the {_snapshotLimits.MaxSnapshotBytes} byte limit");
        }

        if ((probedContentLength is { } probed && expectedLength != probed)
            || !probedValidator.Matches(responseValidator))
        {
            transfer = null!;
            error = new IOException("the snapshot validator or length changed after the HEAD probe");
            return false;
        }

        var validator = probedValidator.Merge(responseValidator);
        var canResume = expectedLength is not null
            && AcceptsByteRanges(response)
            && validator.TryCreateIfRange(out _);
        transfer = new GutenbergSnapshotTransfer(expectedLength, validator, canResume);
        error = new IOException("the snapshot metadata changed");
        return true;
    }

    private bool TryGetPartialTransfer(
        HttpResponseMessage response,
        long offset,
        GutenbergSnapshotTransfer previousTransfer,
        long? probedContentLength,
        GutenbergSnapshotValidator probedValidator,
        out GutenbergSnapshotTransfer transfer,
        out long? expectedSegmentLength,
        out Exception error)
    {
        var range = response.Content.Headers.ContentRange;
        var responseValidator = GutenbergSnapshotValidator.FromResponse(response);

        if (range?.From != offset || range.To is not { } rangeEnd || range.Length is not { } totalLength)
        {
            transfer = null!;
            expectedSegmentLength = null;
            error = new IOException("the snapshot returned an invalid Content-Range");
            return false;
        }

        if (totalLength > _snapshotLimits.MaxSnapshotBytes)
        {
            throw ProviderException.InvalidResponse(
                _providerId,
                $"the catalogue snapshot declares {totalLength} bytes, over the {_snapshotLimits.MaxSnapshotBytes} byte limit");
        }

        expectedSegmentLength = rangeEnd - offset + 1;
        var responseLength = response.Content.Headers.ContentLength;
        if (rangeEnd < offset
            || (responseLength is { } declared && declared != expectedSegmentLength)
            || totalLength != previousTransfer.TotalLength
            || (probedContentLength is { } probedLength && totalLength != probedLength)
            || !previousTransfer.Validator.Matches(responseValidator)
            || !probedValidator.Matches(responseValidator))
        {
            transfer = null!;
            error = new IOException("the snapshot validator or length changed during range recovery");
            return false;
        }

        var validator = previousTransfer.Validator.Merge(responseValidator);
        transfer = new GutenbergSnapshotTransfer(
            totalLength,
            validator,
            previousTransfer.CanResume && validator.TryCreateIfRange(out _));
        error = new IOException("the snapshot range could not be resumed");
        return true;
    }

    private static bool AcceptsByteRanges(HttpResponseMessage response) =>
        response.Headers.AcceptRanges.Any(value => string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase));

    private static bool IsTransientSnapshotStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private TimeSpan RetryDelay(int consecutiveFailures, TimeSpan? retryAfter)
    {
        var exponentialMs = _snapshotDownloadOptions.InitialRetryDelay.TotalMilliseconds
            * Math.Pow(2, Math.Max(0, consecutiveFailures - 1));
        var cappedMs = Math.Min(exponentialMs, _snapshotDownloadOptions.MaxRetryDelay.TotalMilliseconds);
        var jitter = 1 + ((Random.Shared.NextDouble() * 2 - 1) * _snapshotDownloadOptions.RetryJitterRatio);
        var jitteredMs = Math.Min(
            _snapshotDownloadOptions.MaxRetryDelay.TotalMilliseconds,
            Math.Max(0, cappedMs * jitter));
        var backoff = TimeSpan.FromMilliseconds(jitteredMs);
        return retryAfter is { } serverDelay && serverDelay > backoff ? serverDelay : backoff;
    }
}

/// <summary>
/// The validators Gutenberg states for the archive. A conditional resume is only
/// safe when the <c>If-Range</c> value can be built from what the probe saw, so
/// the validator is what makes a range continuation a continuation of the same
/// file rather than a guess.
/// </summary>
internal readonly record struct GutenbergSnapshotValidator(string? ETag, DateTimeOffset? LastModified)
{
    public static GutenbergSnapshotValidator FromResponse(HttpResponseMessage response) =>
        new(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);

    public bool Matches(GutenbergSnapshotValidator candidate) =>
        (ETag is null || string.Equals(ETag, candidate.ETag, StringComparison.Ordinal))
        && (LastModified is null || LastModified == candidate.LastModified);

    public GutenbergSnapshotValidator Merge(GutenbergSnapshotValidator candidate) =>
        new(ETag ?? candidate.ETag, LastModified ?? candidate.LastModified);

    public bool TryCreateIfRange(out RangeConditionHeaderValue condition)
    {
        if (ETag is not null
            && EntityTagHeaderValue.TryParse(ETag, out var entityTag)
            && !entityTag.IsWeak)
        {
            condition = new RangeConditionHeaderValue(entityTag);
            return true;
        }

        if (LastModified is { } lastModified)
        {
            condition = new RangeConditionHeaderValue(lastModified);
            return true;
        }

        condition = null!;
        return false;
    }
}

internal sealed record GutenbergSnapshotTransfer(
    long? TotalLength,
    GutenbergSnapshotValidator Validator,
    bool CanResume);

internal sealed record GutenbergSnapshotDownloadAttempt(
    bool Completed,
    bool RestartFromZero,
    GutenbergSnapshotTransfer? Transfer,
    TimeSpan? RetryAfter,
    Exception? Error,
    long BytesReceived)
{
    public static GutenbergSnapshotDownloadAttempt Success(
        GutenbergSnapshotTransfer transfer,
        long bytesReceived) =>
        new(true, false, transfer, null, null, bytesReceived);

    public static GutenbergSnapshotDownloadAttempt Restart(Exception error) =>
        new(false, true, null, null, error, 0);

    public static GutenbergSnapshotDownloadAttempt Retry(
        GutenbergSnapshotTransfer? transfer,
        TimeSpan? retryAfter,
        Exception error,
        long bytesReceived = 0) =>
        new(false, false, transfer, retryAfter, error, bytesReceived);
}
