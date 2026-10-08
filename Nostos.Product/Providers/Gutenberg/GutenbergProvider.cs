using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Project Gutenberg as a Nostos content source.
///
/// Everything Gutenberg-shaped stops at this boundary. Downstream only ever sees
/// the normalized contracts: an imported book is an ordinary local ebook, and
/// nothing in the library, the reader, notes, work grouping or backups knows
/// this provider exists.
///
/// Uses Gutenberg's machine-readable catalogue rather than the human-facing
/// website: the OPDS/Atom feeds an e-reader would use for live search and
/// detail, and the daily RDF archive for the bulk snapshot a host maintains its
/// own synchronized discovery index from.
/// </summary>
public sealed partial class GutenbergProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy,
    IProviderSnapshotSource
{
    public const string ProviderIdentifier = "gutenberg";

    /// <summary>Named client for the catalogue, so timeouts and headers live in DI.</summary>
    public const string HttpClientName = "gutenberg";

    /// <summary>
    /// Named client for the daily bulk catalogue, with its own long-lived
    /// timeout: the 177 MB archive must not be held to the 20 s request budget
    /// the search client uses.
    /// </summary>
    public const string SnapshotHttpClientName = "gutenberg-snapshot";

    private const string SnapshotTempFilePrefix = "nostos-gutenberg-";

    /// <summary>
    /// Gutenberg's ebook ids are numeric. Validated before the id is used to
    /// build a request path, so a client cannot walk out of the catalogue with a
    /// crafted id.
    /// </summary>
    [GeneratedRegex(@"^\d{1,7}$")]
    private static partial Regex EbookId();

    private const long MaxEbookBytes = 96L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly HttpClient _snapshotHttp;
    private readonly ILogger<GutenbergProvider> _logger;
    private readonly GutenbergSnapshotLimits _snapshotLimits;
    private readonly GutenbergSnapshotDownloadOptions _snapshotDownloadOptions;

    public GutenbergProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<GutenbergProvider> logger,
        GutenbergSnapshotLimits? snapshotLimits = null)
        : this(httpClientFactory, logger, snapshotLimits, null)
    {
    }

    public GutenbergProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<GutenbergProvider> logger,
        GutenbergSnapshotLimits? snapshotLimits,
        GutenbergSnapshotDownloadOptions? snapshotDownloadOptions)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _snapshotHttp = httpClientFactory.CreateClient(SnapshotHttpClientName);
        _logger = logger;
        _snapshotLimits = snapshotLimits ?? new GutenbergSnapshotLimits();
        _snapshotDownloadOptions = snapshotDownloadOptions ?? new GutenbergSnapshotDownloadOptions();
        _snapshotDownloadOptions.Validate();
    }

    public string Id => ProviderIdentifier;

    public string DisplayName => "Project Gutenberg";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Search
        | ProviderCapabilities.ItemRetrieval
        | ProviderCapabilities.EbookAcquisition
        | ProviderCapabilities.CoverArt
        | ProviderCapabilities.RightsInformation;

    /// <summary>
    /// Quoted from the source, not asserted by Nostos: Gutenberg states its
    /// public-domain position for the United States, and that is all this says.
    /// </summary>
    public string? RightsNotice => "Public domain in the USA (Project Gutenberg)";

    public bool EnabledByDefault => true;

    public string? Description => "Public-domain ebooks in many languages.";

    // --- IProviderDownloadPolicy ----------------------------------------
    // Suffix-matched (see ProviderHostPolicy), so www.gutenberg.org and any
    // mirror on the same domain are covered by one entry. A single ebook is at
    // most a couple of dozen megabytes, even illustrated; the headroom is there
    // so a genuinely large volume is not rejected, not because we expect it.
    public IReadOnlyList<string> AllowedHosts => ["gutenberg.org"];

    public long MaxBytesPerPart => MaxEbookBytes;

    public long MaxTotalBytes => MaxEbookBytes;

    /// <summary>One ebook asset per import: Gutenberg exposes no multi-file ebook.</summary>
    public int MaxParts => 1;

    // --- IProviderSearch -------------------------------------------------

    public async Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct)
    {
        var search = Uri.EscapeDataString(query.Query);
        var path = $"/ebooks/search.opds/?query={search}";

        // The catalogue pages with a 1-based start index.
        if (query.Offset > 0)
            path += $"&start_index={query.Offset + 1}";

        var feed = await LoadAsync(path, ct);
        if (feed is null)
            return new ProviderSearchPage([], HasMore: false);

        var books = GutenbergCatalog.ParseSearch(feed);
        var perPage = GutenbergCatalog.ItemsPerPage(feed);

        var items = books.Select(book => ToProviderItem(book, includeAssets: false)).ToList();

        return new ProviderSearchPage(
            Items: items,
            // The feed reports no total, so a full page is the only signal that
            // there may be more.
            HasMore: books.Count >= perPage && items.Count > 0,
            Notice: null);
    }

    // --- IProviderCatalog ------------------------------------------------

    public async Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct)
    {
        var book = await LoadBookAsync(externalId, ct);
        return book is null ? null : ToProviderItem(book, includeAssets: true);
    }

    // --- IProviderSnapshotSource ------------------------------------------

    /// <summary>
    /// Reads the whole catalogue from Gutenberg's daily machine-readable
    /// archive (a zipped tar of one RDF file per ebook). The probe is a HEAD,
    /// so a host's conditional re-check never transfers the 177 MB body; the
    /// archive is downloaded and parsed lazily on the first enumeration, and
    /// its temp file is deleted when enumeration ends, whatever ended it.
    ///
    /// The archive changes once a day and carries Last-Modified, so a
    /// not-modified probe is the normal outcome of the daily re-check. The live
    /// search cache is deliberately not used: this must see the source's own
    /// validators and the full catalogue rather than search-shaped pages.
    /// </summary>
    public async Task<ProviderSnapshot> ReadAsync(ProviderSnapshotRequest request, CancellationToken ct)
    {
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Head, GutenbergCatalog.SnapshotPath);
            ApplyValidators(probe, request);

            using var probeTimeout = new CancellationTokenSource(_snapshotDownloadOptions.MaxDuration);
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, probeTimeout.Token);
            using var response = await _snapshotHttp.SendAsync(
                probe,
                HttpCompletionOption.ResponseHeadersRead,
                probeCancellation.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return ProviderSnapshot.NotModified(
                    etag: request.ETag ?? response.Headers.ETag?.ToString(),
                    lastModified: request.IfModifiedSince ?? response.Content.Headers.LastModified);
            }

            if (!response.IsSuccessStatusCode)
                throw ProviderException.UnavailableFor(Id, "the catalogue snapshot answered HTTP " + (int)response.StatusCode);

            return new ProviderSnapshot(
                ProviderSnapshotStatus.Updated,
                ReadSnapshotItemsAsync(
                    response.Content.Headers.ContentLength,
                    SnapshotValidator.FromResponse(response)),
                ETag: response.Headers.ETag?.ToString(),
                LastModified: response.Content.Headers.LastModified);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
        catch (IOException ex)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
    }

    // --- IProviderAcquisitionPlanner -------------------------------------

    public async Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
        ProviderAcquisitionRequest request,
        CancellationToken ct)
    {
        // The catalogue is re-read here rather than reusing whatever the UI last
        // saw: the plan is what actually gets downloaded, so it is resolved from
        // the source as it is now, and a stale asset id fails loudly instead of
        // fetching a URL that is no longer offered.
        var book = await LoadBookAsync(request.ExternalId, ct);
        if (book is null)
            return null;

        var asset = request.AssetId is null
            ? book.Assets.FirstOrDefault(a => a.IsPreferred) ?? book.Assets.FirstOrDefault()
            : book.Assets.FirstOrDefault(a => string.Equals(a.Id, request.AssetId, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
            throw ProviderException.AssetUnavailableFor(Id, book.Id, request.AssetId ?? "ebook");

        return new ProviderAcquisitionPlan(
            ProviderId: Id,
            ExternalId: book.Id,
            Asset: new ProviderAsset(
                Id: asset.Id,
                Kind: ProviderMediaKind.Ebook,
                Label: asset.Label,
                SourceFormat: asset.SourceFormat,
                SizeBytes: asset.SizeBytes,
                IsPreferred: asset.IsPreferred),
            Metadata: new ProviderMetadata(
                Title: book.Title,
                Author: book.Author,
                Description: book.Description,
                Language: book.Language,
                Categories: book.Categories),
            Parts:
            [
                new ProviderDownloadPart(
                    Url: asset.Url,
                    FileExtension: asset.FileExtension,
                    ExpectedBytes: asset.SizeBytes,
                    Label: asset.Label),
            ],
            Output: new ProviderOutput(asset.FileExtension, asset.SourceFormat, asset.OutputLabel),
            Cover: GutenbergCatalog.CoverFor(book.Id),
            Source: new ProviderSourceInfo(
                ItemUrl: $"{GutenbergCatalog.BaseUrl}/ebooks/{book.Id}",
                RightsStatement: book.Rights,
                RightsUrl: $"{GutenbergCatalog.BaseUrl}/policy/terms_of_use.html"));
    }

    // --- internals -------------------------------------------------------

    private async Task<GutenbergBook?> LoadBookAsync(string externalId, CancellationToken ct)
    {
        var id = externalId?.Trim() ?? string.Empty;

        // A non-numeric id cannot address an ebook, and is rejected before it
        // reaches a request path rather than after.
        if (!EbookId().IsMatch(id))
            return null;

        var feed = await LoadAsync($"/ebooks/{id}.opds", ct);
        return feed is null ? null : GutenbergCatalog.ParseDetail(feed, id);
    }

    private async Task<XDocument?> LoadAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);

        // The catalogue answers 404 for an unknown ebook; that is an ordinary
        // "no such item", not a source failure.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;

        if (!response.IsSuccessStatusCode)
            throw ProviderException.UnavailableFor(Id, $"the catalogue answered HTTP {(int)response.StatusCode}");

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }
        catch (XmlException ex)
        {
            // A maintenance page or an HTML error document: worth reporting as a
            // source problem, never worth passing off as an empty catalogue.
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    /// <summary>
    /// The snapshot items. Nothing exists until the first enumeration: the
    /// archive is downloaded on the first <c>MoveNextAsync</c> and the temp file
    /// is removed in the <c>finally</c>, which runs when the consumer breaks,
    /// throws or cancels as well as on normal completion.
    /// </summary>
    private async IAsyncEnumerable<ProviderItem> ReadSnapshotItemsAsync(
        long? probedContentLength,
        SnapshotValidator probedValidator,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            SnapshotTempFilePrefix + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await DownloadSnapshotAsync(path, probedContentLength, probedValidator, ct);

            await using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = OpenSnapshotArchive(file);

            var tarEntry = zip.Entries.FirstOrDefault(entry =>
                entry.FullName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                ?? throw ProviderException.InvalidResponse(Id, "the snapshot archive has no tar catalog");

            await using var tarStream = OpenSnapshotTar(tarEntry);
            using var tar = new TarReader(tarStream);

            while (await ReadSnapshotItemAsync(tar, ct) is { } item)
                yield return item;
        }
        finally
        {
            TryDeleteSnapshotFile(path);
        }
    }

    private async Task DownloadSnapshotAsync(
        string path,
        long? probedContentLength,
        SnapshotValidator probedValidator,
        CancellationToken ct)
    {
        if (probedContentLength > _snapshotLimits.MaxSnapshotBytes)
        {
            throw ProviderException.InvalidResponse(
                Id,
                $"the catalogue snapshot declares {probedContentLength} bytes, over the {_snapshotLimits.MaxSnapshotBytes} byte limit");
        }

        using var deadline = new CancellationTokenSource(_snapshotDownloadOptions.MaxDuration);
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var stopwatch = Stopwatch.StartNew();
        SnapshotTransfer? transfer = null;
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

            SnapshotDownloadAttempt result;
            try
            {
                attemptsMade++;
                result = await DownloadSnapshotAttemptAsync(
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

        throw ProviderException.UnavailableFor(Id, reason);
    }

    private async Task<SnapshotDownloadAttempt> DownloadSnapshotAttemptAsync(
        string path,
        long offset,
        bool useRange,
        SnapshotTransfer? previousTransfer,
        long? probedContentLength,
        SnapshotValidator probedValidator,
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
            response = await _snapshotHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                downloadToken);
        }
        catch (OperationCanceledException) when (!downloadToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            return SnapshotDownloadAttempt.Retry(previousTransfer, null, new TimeoutException("the snapshot request timed out"));
        }
        catch (HttpRequestException ex)
        {
            return SnapshotDownloadAttempt.Retry(previousTransfer, null, ex);
        }
        catch (IOException ex)
        {
            return SnapshotDownloadAttempt.Retry(previousTransfer, null, ex);
        }

        using (response)
        {
            if (IsTransientSnapshotStatus(response.StatusCode))
            {
                return SnapshotDownloadAttempt.Retry(
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
                        return SnapshotDownloadAttempt.Restart(fullError);
                    }

                    return await CopySnapshotResponseAsync(
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
                        return SnapshotDownloadAttempt.Restart(partialError);
                    }

                    return await CopySnapshotResponseAsync(
                        path,
                        response,
                        offset,
                        append: true,
                        partialTransfer,
                        segmentLength,
                        downloadToken);
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    return SnapshotDownloadAttempt.Restart(new IOException("the snapshot rejected the requested byte range"));

                throw ProviderException.UnavailableFor(
                    Id,
                    "the catalogue snapshot answered HTTP " + (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.PartialContent)
                return SnapshotDownloadAttempt.Restart(new IOException("the snapshot returned a partial response without a range request"));

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw ProviderException.UnavailableFor(
                    Id,
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
                return SnapshotDownloadAttempt.Restart(error);
            }

            return await CopySnapshotResponseAsync(
                path,
                response,
                offset: 0,
                append: false,
                transfer,
                contentLength,
                downloadToken);
        }
    }

    private async Task<SnapshotDownloadAttempt> CopySnapshotResponseAsync(
        string path,
        HttpResponseMessage response,
        long offset,
        bool append,
        SnapshotTransfer transfer,
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
                    return SnapshotDownloadAttempt.Restart(new IOException("the partial snapshot file changed before resume"));
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
                    throw ProviderException.InvalidResponse(Id, "the snapshot response exceeded its declared range length");
                }

                if (offset + written + read > _snapshotLimits.MaxSnapshotBytes)
                {
                    throw ProviderException.InvalidResponse(
                        Id,
                        $"the catalogue snapshot passed the {_snapshotLimits.MaxSnapshotBytes} byte limit");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
            }

            await destination.FlushAsync(ct);

            if (expectedSegmentLength is { } declaredSegment && written < declaredSegment)
            {
                return SnapshotDownloadAttempt.Retry(
                    transfer,
                    null,
                    new EndOfStreamException($"the snapshot response ended after {written} of {declaredSegment} declared bytes"),
                    written);
            }

            var finalLength = offset + written;
            if (transfer.TotalLength is not { } totalLength)
            {
                throw ProviderException.InvalidResponse(Id, "the catalogue snapshot has no declared content length");
            }

            if (finalLength > totalLength)
                throw ProviderException.InvalidResponse(Id, "the snapshot exceeded its declared content length");

            if (finalLength < totalLength)
            {
                return SnapshotDownloadAttempt.Retry(
                    transfer,
                    null,
                    new EndOfStreamException($"the snapshot ended at {finalLength} of {totalLength} declared bytes"),
                    written);
            }

            return SnapshotDownloadAttempt.Success(transfer, written);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SnapshotDownloadAttempt.Retry(
                transfer,
                null,
                new TimeoutException("the snapshot stream read timed out"),
                written);
        }
        catch (HttpRequestException ex)
        {
            return SnapshotDownloadAttempt.Retry(transfer, null, ex, written);
        }
        catch (IOException ex)
        {
            return SnapshotDownloadAttempt.Retry(transfer, null, ex, written);
        }
    }

    private bool TryGetFullTransfer(
        HttpResponseMessage response,
        long? probedContentLength,
        SnapshotValidator probedValidator,
        out SnapshotTransfer transfer,
        out long? expectedLength,
        out Exception error)
    {
        var responseLength = response.Content.Headers.ContentLength;
        expectedLength = responseLength ?? probedContentLength;
        var responseValidator = SnapshotValidator.FromResponse(response);

        if (expectedLength > _snapshotLimits.MaxSnapshotBytes)
        {
            throw ProviderException.InvalidResponse(
                Id,
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
        transfer = new SnapshotTransfer(expectedLength, validator, canResume);
        error = new IOException("the snapshot metadata changed");
        return true;
    }

    private bool TryGetPartialTransfer(
        HttpResponseMessage response,
        long offset,
        SnapshotTransfer previousTransfer,
        long? probedContentLength,
        SnapshotValidator probedValidator,
        out SnapshotTransfer transfer,
        out long? expectedSegmentLength,
        out Exception error)
    {
        var range = response.Content.Headers.ContentRange;
        var responseValidator = SnapshotValidator.FromResponse(response);

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
                Id,
                $"the catalogue snapshot declares {totalLength} bytes, over the {_snapshotLimits.MaxSnapshotBytes} byte limit");
        }

        expectedSegmentLength = rangeEnd - offset + 1;
        var responseLength = response.Content.Headers.ContentLength;
        if (rangeEnd < offset
            || (responseLength is { } declared && declared != expectedSegmentLength)
            || totalLength != previousTransfer.TotalLength
            || (probedContentLength is { } probed && totalLength != probed)
            || !previousTransfer.Validator.Matches(responseValidator)
            || !probedValidator.Matches(responseValidator))
        {
            transfer = null!;
            error = new IOException("the snapshot validator or length changed during range recovery");
            return false;
        }

        var validator = previousTransfer.Validator.Merge(responseValidator);
        transfer = new SnapshotTransfer(
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

    private readonly record struct SnapshotValidator(string? ETag, DateTimeOffset? LastModified)
    {
        public static SnapshotValidator FromResponse(HttpResponseMessage response) =>
            new(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);

        public bool Matches(SnapshotValidator candidate) =>
            (ETag is null || string.Equals(ETag, candidate.ETag, StringComparison.Ordinal))
            && (LastModified is null || LastModified == candidate.LastModified);

        public SnapshotValidator Merge(SnapshotValidator candidate) =>
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

    private sealed record SnapshotTransfer(long? TotalLength, SnapshotValidator Validator, bool CanResume);

    private sealed record SnapshotDownloadAttempt(
        bool Completed,
        bool RestartFromZero,
        SnapshotTransfer? Transfer,
        TimeSpan? RetryAfter,
        Exception? Error,
        long BytesReceived)
    {
        public static SnapshotDownloadAttempt Success(SnapshotTransfer transfer, long bytesReceived) =>
            new(true, false, transfer, null, null, bytesReceived);

        public static SnapshotDownloadAttempt Restart(Exception error) =>
            new(false, true, null, null, error, 0);

        public static SnapshotDownloadAttempt Retry(
            SnapshotTransfer? transfer,
            TimeSpan? retryAfter,
            Exception error,
            long bytesReceived = 0) =>
            new(false, false, transfer, retryAfter, error, bytesReceived);
    }

    /// <summary>
    /// One item per call, skipping anything in the tar that is not an ebook RDF
    /// file and stopping the enumeration at the end of the archive. Parse
    /// failures are translated here because an iterator cannot catch and
    /// <c>yield</c> in the same block.
    /// </summary>
    private async Task<ProviderItem?> ReadSnapshotItemAsync(TarReader tar, CancellationToken ct)
    {
        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct);
            }
            catch (Exception ex) when (IsArchiveFailure(ex))
            {
                throw ProviderException.InvalidResponse(Id, ex.Message);
            }

            if (entry is null)
                return null;

            ct.ThrowIfCancellationRequested();

            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                continue;

            if (GutenbergCatalog.SnapshotIdFromEntryName(entry.Name) is not { } id)
                continue;

            // The tar header declares the member's length; reject an
            // implausible one before XDocument can materialise it, so a zip
            // bomb cannot exhaust memory through a single record.
            if (entry.Length > _snapshotLimits.MaxMemberBytes)
            {
                throw ProviderException.InvalidResponse(
                    Id,
                    $"snapshot entry '{entry.Name}' is over the {_snapshotLimits.MaxMemberBytes} byte member limit");
            }

            GutenbergBook? book;
            try
            {
                var document = await XDocument.LoadAsync(entry.DataStream, LoadOptions.None, ct);
                book = GutenbergCatalog.ParseSnapshot(id, document);
            }
            catch (XmlException ex)
            {
                throw ProviderException.InvalidResponse(Id, $"snapshot entry '{entry.Name}': {ex.Message}");
            }
            catch (Exception ex) when (IsArchiveFailure(ex))
            {
                // The member's declared length can outrun the bytes that are
                // actually there; that is a corrupt response, not a host error.
                throw ProviderException.InvalidResponse(Id, $"snapshot entry '{entry.Name}': {ex.Message}");
            }

            if (book is not null)
                return ToProviderItem(book, includeAssets: false);
        }
    }

    private ZipArchive OpenSnapshotArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    private Stream OpenSnapshotTar(ZipArchiveEntry entry)
    {
        try
        {
            return entry.Open();
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    /// <summary>
    /// A truncated or corrupt archive: <see cref="EndOfStreamException"/> and
    /// other <see cref="IOException"/>s when the stream ends mid-structure,
    /// <see cref="InvalidDataException"/> for bad headers and unsupported
    /// compression. None of these is a host failure, so all of them become
    /// <c>provider_response_invalid</c>. Cancellation is not an IOException and
    /// deliberately keeps propagating.
    /// </summary>
    private static bool IsArchiveFailure(Exception ex) => ex is IOException or InvalidDataException;

    private void TryDeleteSnapshotFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover 177 MB temp file is worth knowing about; it must not
            // mask the enumeration result that brought us here.
            _logger.LogWarning(ex, "Could not delete the Gutenberg snapshot file {Path}.", path);
        }
    }

    private static void ApplyValidators(HttpRequestMessage request, ProviderSnapshotRequest snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ETag)
            && EntityTagHeaderValue.TryParse(snapshot.ETag, out var entityTag))
        {
            request.Headers.IfNoneMatch.Add(entityTag);
        }

        if (snapshot.IfModifiedSince is { } ifModifiedSince)
            request.Headers.IfModifiedSince = ifModifiedSince;
    }

    private ProviderItem ToProviderItem(GutenbergBook book, bool includeAssets) => new(
        ProviderId: Id,
        ExternalId: book.Id,
        MediaKind: ProviderMediaKind.Ebook,
        Metadata: new ProviderMetadata(
            Title: book.Title,
            Author: book.Author,
            Description: book.Description,
            Language: book.Language,
            Categories: book.Categories),
        Assets: includeAssets
            ? book.Assets
                .Select(asset => new ProviderAsset(
                    Id: asset.Id,
                    Kind: ProviderMediaKind.Ebook,
                    Label: asset.Label,
                    SourceFormat: asset.SourceFormat,
                    SizeBytes: asset.SizeBytes,
                    IsPreferred: asset.IsPreferred))
                .ToList()
            : [],
        Cover: GutenbergCatalog.CoverFor(book.Id),
        Source: new ProviderSourceInfo(
            ItemUrl: $"{GutenbergCatalog.BaseUrl}/ebooks/{book.Id}",
            RightsStatement: book.Rights,
            RightsUrl: $"{GutenbergCatalog.BaseUrl}/policy/terms_of_use.html"),
        PartCount: null);
}
