using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Nostos.Product.BookText;
using SixLabors.ImageSharp;

namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// Turns a chosen external item into an ordinary local Nostos book.
///
/// This is the only place the two halves of the feature meet: it knows how to
/// drive a provider, and it knows how to commit to the library, but it holds no
/// knowledge of any particular source — and no provider holds any knowledge of
/// the library.
/// </summary>
public interface IAcquisitionService
{
    Task<AcquisitionResult> AcquireAsync(
        AcquisitionRequest request,
        IProgress<AcquisitionProgress>? progress,
        CancellationToken ct);
}

public sealed class AcquisitionService(
    IProviderRegistry registry,
    IProviderContentDownloader downloader,
    IBookAssetStorage storage,
    ILibraryService library,
    IDbContextFactory<NostosDbContext> contexts,
    IWebHostEnvironment environment,
    ITranscodeLimiter transcodeLimiter,
    IOptions<AcquisitionOptions> options,
    ILogger<AcquisitionService> logger,
    IAcquisitionWorkingRootProvider? workingRootProvider = null,
    IBookTextIngestionScheduler? bookTextScheduler = null) : IAcquisitionService
{
    private readonly AcquisitionOptions _options = options.Value;

    public async Task<AcquisitionResult> AcquireAsync(
        AcquisitionRequest request,
        IProgress<AcquisitionProgress>? progress,
        CancellationToken ct)
    {
        var sink = new ProgressSink(progress);

        try
        {
            return await RunAsync(request, sink, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AcquisitionException ex)
        {
            logger.LogWarning("Acquisition of {Provider}/{ExternalId} failed: {Code}", request.ProviderId, request.ExternalId, ex.Code);
            return AcquisitionResult.Failed(ex.Code, ex.Message);
        }
        catch (ProviderException ex)
        {
            logger.LogWarning("Acquisition of {Provider}/{ExternalId} failed: {Code}", request.ProviderId, request.ExternalId, ex.Code);
            return AcquisitionResult.Failed(ex.Code, ex.Message);
        }
        catch (ProviderDownloadException ex)
        {
            logger.LogWarning("Acquisition of {Provider}/{ExternalId} failed: {Code}", request.ProviderId, request.ExternalId, ex.Code);
            return AcquisitionResult.Failed(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Acquisition of {Provider}/{ExternalId} failed unexpectedly with {ExceptionType}; details suppressed.",
                request.ProviderId,
                request.ExternalId,
                ex.GetType().Name);
            return AcquisitionResult.Failed("acquisition_failed", "The import failed unexpectedly. See the server log for details.");
        }
    }

    private async Task<AcquisitionResult> RunAsync(
        AcquisitionRequest request,
        ProgressSink progress,
        CancellationToken ct)
    {
        // --- 1. Resolve the provider -------------------------------------
        var provider = registry.Find(request.ProviderId)
            ?? throw new AcquisitionException(
                AcquisitionException.ProviderUnknown,
                $"No content provider is registered as '{request.ProviderId}'.");

        if (provider.Planner is null || provider.DownloadPolicy is null)
            throw new AcquisitionException(
                AcquisitionException.ProviderCannotAcquire,
                $"{provider.Provider.DisplayName} cannot acquire content.");

        if (string.IsNullOrWhiteSpace(request.ExternalId) || request.ExternalId.Length > 128)
            throw new AcquisitionException(
                AcquisitionException.InvalidRequest, "A valid external item id is required.");

        var policy = provider.DownloadPolicy;

        // --- 2. Cheap duplicate check when the caller named an asset ------
        // Only possible when the key is fully specified: with no asset the
        // resolved id is not known until the plan comes back, and guessing it
        // here would either miss a duplicate or reject a different asset.
        if (!string.IsNullOrWhiteSpace(request.AssetId))
        {
            var known = await FindProvenanceAsync(provider.Id, request.ExternalId, request.AssetId, ct);
            if (known is { } knownBookId)
                return await AlreadyAcquiredAsync(knownBookId, provider.Id, progress, ct);
        }

        // --- 3. Plan (the only step that talks to the source catalogue) ---
        progress.Report(new AcquisitionProgress("resolving", 3));
        var plan = await provider.Planner.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest(request.ExternalId, request.AssetId), ct)
            ?? throw new AcquisitionException(
                AcquisitionException.ItemNotFound,
                $"{provider.Provider.DisplayName} has no item '{request.ExternalId}'.");

        // The user's own edits (an import started from a prefilled form) overlay
        // the source's metadata from here on, so the created book, its cover and
        // the success message all agree. Identity is deliberately NOT merged —
        // see MergeMetadata.
        var metadata = MergeMetadata(plan.Metadata, request.MetadataOverrides);

        if (plan.Parts.Count == 0)
            throw new AcquisitionException(
                AcquisitionException.NoAssets,
                $"{provider.Provider.DisplayName} offers no downloadable asset for '{request.ExternalId}'.");

        if (plan.Parts.Count > policy.MaxParts)
            throw new AcquisitionException(
                AcquisitionException.TooManyParts,
                $"This item has {plan.Parts.Count} parts, above the {policy.MaxParts} part limit.");

        if (string.IsNullOrWhiteSpace(metadata.Title))
            throw new AcquisitionException(
                AcquisitionException.InvalidRequest, "The source returned no title for this item.");

        // --- 4. Authoritative duplicate check, now the asset id is known --
        var existing = await FindProvenanceAsync(plan.ProviderId, plan.ExternalId, plan.Asset.Id, ct);
        if (existing is { } existingBookId)
            return await AlreadyAcquiredAsync(existingBookId, provider.Id, progress, ct);

        // --- 5. Is the library already holding this work locally? ---------
        // A read-only probe, deliberately before any download: re-importing a
        // work the user already has a file for must not cost them 350 MB of
        // bandwidth. Only an unambiguous answer short-circuits; anything else
        // falls through to the real create-or-match at commit time.
        progress.Report(new AcquisitionProgress("checking", 6));
        var expectedType = plan.Asset.Kind == ProviderMediaKind.Audiobook ? "audiobook" : "ebook";
        if (await AlreadyHasLocalFileAsync(plan, expectedType, ct) is { } heldId)
        {
            var held = await library.GetBookAsync(heldId, ct);
            return AcquisitionResult.AlreadyInLibrary(
                heldId,
                DataOf(held) as BookDto,
                "This book is already in your library with a local file; nothing was imported.");
        }

        // --- 6. Create on confirm: establish the book row before download --
        var (bookId, createdByUs, matchedBook) =
            await CreateOrMatchAsync(provider, plan, request, expectedType, ct);

        if (!createdByUs && matchedBook?.HasFile == true)
        {
            var current = await library.GetBookAsync(bookId, ct);
            return AcquisitionResult.AlreadyInLibrary(
                bookId,
                DataOf(current) as BookDto,
                "This book is already in your library with a local file; nothing was imported.");
        }

        await library.SetBookStatusAsync(bookId, BookStatus.Downloading, statusMessage: null, ct);
        progress.Report(new AcquisitionProgress("downloading", 10, BookId: bookId));

        // --- 7. Acquire into an isolated staging directory -----------------
        var rootProvider =
            workingRootProvider ?? new DefaultAcquisitionWorkingRootProvider();
        var workingRoot = Path.Combine(
            rootProvider.ResolveWorkingRoot(
                environment.ContentRootPath,
                _options),
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workingRoot);
        try
        {
            EnsureFreeSpace(workingRoot, EstimatedBytes(plan));

            var parts = await DownloadPartsAsync(provider.Id, plan, policy, workingRoot, progress, ct);

            await library.SetBookStatusAsync(bookId, BookStatus.Transcoding, statusMessage: null, ct);
            var artifact = await AssembleAsync(provider, plan, parts, workingRoot, progress, ct);
            ValidateArtifact(artifact, plan);

            var cover = request.IncludeCover
                ? await TryDownloadCoverAsync(plan, policy, ct)
                : null;

            // --- 8. Commit ------------------------------------------------
            return await CommitAsync(provider, plan, artifact, cover, request, bookId, createdByUs, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A CANCELLED import must still delete the row when we created it.
            if (createdByUs)
            {
                try
                {
                    await library.DeleteBookAsync(bookId, CancellationToken.None);
                }
                catch (Exception delEx)
                {
                    logger.LogWarning(
                        "Could not delete cancelled book {BookId}; exception type {ExceptionType}. Details suppressed.",
                        bookId,
                        delEx.GetType().Name);
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            // On any acquisition failure set Failed plus a short StatusMessage and KEEP THE ROW.
            var failMessage = ex switch
            {
                AcquisitionException aex => aex.Message,
                ProviderException pex => pex.Message,
                ProviderDownloadException dex => dex.Message,
                _ => "The import failed unexpectedly."
            };

            try
            {
                await library.SetBookStatusAsync(bookId, BookStatus.Failed, failMessage, CancellationToken.None);
            }
            catch (Exception statusEx)
            {
                logger.LogWarning(
                    "Could not set Failed status on book {BookId}; exception type {ExceptionType}. Details suppressed.",
                    bookId,
                    statusEx.GetType().Name);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    // ------------------------------------------------------------------
    // Staging
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<AcquisitionPart>> DownloadPartsAsync(
        string providerId,
        ProviderAcquisitionPlan plan,
        IProviderDownloadPolicy policy,
        string workingRoot,
        ProgressSink progress,
        CancellationToken ct)
    {
        progress.Report(new AcquisitionProgress("downloading", 10));

        var totalParts = plan.Parts.Count;
        var done = 0;
        long bytesWritten = 0;
        var declaredTotal = plan.Parts.Sum(p => p.ExpectedBytes ?? 0);
        var results = new AcquisitionPart[totalParts];

        using var throttle = new SemaphoreSlim(_options.ClampDownloadConcurrency());

        // One part failing must not leave the other forty downloading into a
        // directory that is about to be deleted: the first failure cancels the
        // siblings so the whole acquisition winds down promptly.
        using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // ...but cancelling the siblings means several tasks now fail with a
        // cancellation that is a *consequence* of the first failure. Without
        // this the error code reported to the user would be whichever task
        // happened to lose the race.
        Exception? primaryFailure = null;

        var tasks = plan.Parts.Select((part, index) => DownloadOneAsync(index)).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (!ct.IsCancellationRequested && primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }

        return results;

        async Task DownloadOneAsync(int index)
        {
            try
            {
                await throttle.WaitAsync(abandoned.Token);
            }
            catch (OperationCanceledException)
            {
                // A sibling failed and the acquisition is unwinding; the
                // original failure is the one worth reporting.
                throw new OperationCanceledException(ct);
            }

            try
            {
                var part = plan.Parts[index];

                // The file name is generated here, never taken from the
                // source: a remote string must not be able to influence a path.
                var extension = NormalizeExtension(part.FileExtension);
                var destination = Path.Combine(workingRoot, $"part-{index:0000}{extension}");

                var budget = policy.MaxTotalBytes - Interlocked.Read(ref bytesWritten);
                if (budget <= 0)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.TooLarge,
                        $"This acquisition has passed its {policy.MaxTotalBytes} byte total limit.");

                var partProgress = new Progress<long>(written => progress.ReportDownload(index, written, plan.Parts));

                var written = await downloader.DownloadAsync(
                    part.Url, destination, policy, budget, partProgress, abandoned.Token);

                results[index] = new AcquisitionPart(destination, extension, written);
                Interlocked.Add(ref bytesWritten, written);
                var completed = Interlocked.Increment(ref done);

                progress.Report(DownloadProgress(completed, totalParts, bytesWritten, declaredTotal));
            }
            catch (Exception ex)
            {
                var siblingCancellation = ex is OperationCanceledException
                    && abandoned.IsCancellationRequested
                    && !ct.IsCancellationRequested;

                if (!siblingCancellation)
                    Interlocked.CompareExchange(ref primaryFailure, ex, null);

                await abandoned.CancelAsync();
                throw;
            }
            finally
            {
                throttle.Release();
            }
        }
    }

    private static AcquisitionProgress DownloadProgress(int completed, int totalParts, long bytesWritten, long declaredTotal)
    {
        // Percentage is measured in parts when the source does not declare
        // sizes, and in bytes when it does (which is the case for multi-part
        // audio, where playtime implies a size).
        var fraction = declaredTotal > 0
            ? Math.Min(1.0, (double)bytesWritten / declaredTotal)
            : (double)completed / totalParts;

        var percent = 10 + (int)(fraction * 55);
        return new AcquisitionProgress("downloading", Math.Clamp(percent, 10, 65), $"{completed}/{totalParts} files");
    }

    private async Task<AcquisitionArtifact> AssembleAsync(
        ProviderRegistration provider,
        ProviderAcquisitionPlan plan,
        IReadOnlyList<AcquisitionPart> parts,
        string workingRoot,
        ProgressSink progress,
        CancellationToken ct)
    {
        progress.Report(new AcquisitionProgress("validating", 68));
        ValidateParts(parts, plan);

        if (provider.Assembler is null)
        {
            // A provider that delivers one usable file needs no assembly step,
            // and the part IS the artifact. Declaring RequiresAssembly without
            // an assembler is rejected at startup, so this branch is safe.
            var single = parts[0];
            return new AcquisitionArtifact(single.FilePath, single.FileExtension, plan.Output.ContentType);
        }

        progress.Report(new AcquisitionProgress("assembling", 72, plan.Output.Label));

        // Serialised across the process: an encode of this size already
        // saturates the box, so two at once would stall everything else.
        using var slot = await transcodeLimiter.AcquireAsync(ct);

        var artifact = await provider.Assembler.AssembleAsync(
            new AcquisitionAssemblyContext(plan, parts, workingRoot), ct);

        progress.Report(new AcquisitionProgress("assembling", 92, plan.Output.Label));
        return artifact;
    }

    private static void ValidateParts(IReadOnlyList<AcquisitionPart> parts, ProviderAcquisitionPlan plan)
    {
        if (parts.Count != plan.Parts.Count)
            throw new AcquisitionException(
                AcquisitionException.PartMissing,
                $"Expected {plan.Parts.Count} downloads but received {parts.Count}.");

        foreach (var part in parts)
        {
            if (part.Bytes <= 0 || !File.Exists(part.FilePath))
                throw new AcquisitionException(
                    AcquisitionException.PartMissing,
                    "One of the source's files arrived empty.");
        }
    }

    private static void ValidateArtifact(AcquisitionArtifact artifact, ProviderAcquisitionPlan plan)
    {
        if (!File.Exists(artifact.FilePath))
            throw new AcquisitionException(
                AcquisitionException.AssemblyFailed, "The assembled file was not produced.");

        var length = new FileInfo(artifact.FilePath).Length;
        if (length <= 0)
            throw new AcquisitionException(
                AcquisitionException.AssemblyFailed, "The assembled file is empty.");

        // The artifact's extension decides what the library stores and which
        // reader eventually opens it, so a mismatch with the plan is a bug worth
        // stopping on rather than a file that lands as the wrong format.
        var expected = NormalizeExtension(plan.Output.FileExtension);
        if (!string.Equals(artifact.FileExtension, expected, StringComparison.OrdinalIgnoreCase))
            throw new AcquisitionException(
                AcquisitionException.AssemblyFailed,
                $"Assembly produced a '{artifact.FileExtension}' file where '{expected}' was expected.");
    }

    /// <summary>Cover artwork is small enough to hold in memory, unlike a book file.</summary>
    private const long MaxCoverBytes = 12L * 1024 * 1024;

    private async Task<byte[]?> TryDownloadCoverAsync(
        ProviderAcquisitionPlan plan,
        IProviderDownloadPolicy policy,
        CancellationToken ct)
    {
        if (plan.Cover is null)
            return null;

        try
        {
            var bytes = await downloader.DownloadBytesAsync(plan.Cover.Url, policy, MaxCoverBytes, ct);

            // A cover is rendered by the image pipeline, so a source that hands
            // back an HTML error page must not get stored as one: that would
            // poison the thumbnail cache and every later list render.
            using var probe = new MemoryStream(bytes);
            var info = await Image.IdentifyAsync(probe, ct);
            if (info is null)
            {
                logger.LogWarning("Cover art for {ExternalId} was not a recognisable image; skipping it.", plan.ExternalId);
                return null;
            }

            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cover art is a nicety. It must never cost the user the book.
            logger.LogWarning(
                "Could not fetch cover art for {ExternalId}; importing without it. Exception type {ExceptionType}; details suppressed.",
                plan.ExternalId,
                ex.GetType().Name);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Commit
    // ------------------------------------------------------------------

    private async Task<AcquisitionResult> CommitAsync(
        ProviderRegistration provider,
        ProviderAcquisitionPlan plan,
        AcquisitionArtifact artifact,
        byte[]? cover,
        AcquisitionRequest request,
        Guid bookId,
        bool createdByUs,
        ProgressSink progress,
        CancellationToken ct)
    {
        progress.Report(new AcquisitionProgress("importing", 94, BookId: bookId));

        // Commit the caller-owned staging artifact into durable storage. The
        // local provider can turn this into a rename; remote storage streams the
        // file and removes scratch only after success.
        var storedBytes = new FileInfo(artifact.FilePath).Length;
        string? staged;
        try
        {
            staged = await storage.AdoptBookFileAsync(bookId, artifact.FilePath, $"book{artifact.FileExtension}", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Storing the acquired file for {ExternalId} failed with {ExceptionType}; details suppressed.",
                plan.ExternalId,
                ex.GetType().Name);
            await RollbackAsync(bookId, createdByUs, storedNothing: true, plan, ct);
            await library.SetBookStatusAsync(bookId, BookStatus.Failed, "The file could not be stored, so nothing was imported.", CancellationToken.None);
            return AcquisitionResult.Failed(
                "storage_failed", "The file could not be stored, so nothing was imported.");
        }

        // Stored BEFORE the provenance write so the file name can travel with it:
        // one SaveChanges, and the row can never point at a cover that is not on
        // disk. Cover art stays a nicety — a failure here is not a failed import.
        var coverFileName = await TryAttachCoverAsync(bookId, cover, plan, ct);

        var attach = await library.AttachAcquiredAssetAsync(new LibraryAttachAcquiredAssetRequest(
            ClientId: "acquisition",
            IdempotencyKey: $"acquire-{plan.ProviderId}-{plan.ExternalId}-{plan.Asset.Id}-{Guid.NewGuid():N}",
            BookId: bookId,
            FileName: Path.GetFileName(staged),
            ProviderId: plan.ProviderId,
            ProviderDisplayName: provider.Provider.DisplayName,
            ExternalId: plan.ExternalId,
            AssetId: plan.Asset.Id,
            AssetFormat: plan.Asset.SourceFormat,
            SourceUrl: plan.Source?.ItemUrl,
            RightsStatement: plan.Source?.RightsStatement,
            AcquiredAt: DateTime.UtcNow,
            // What the assembler measured on the produced file beats what the
            // source claimed: for an audiobook that is the difference between
            // chapter markers that line up and ones that drift.
            Duration: artifact.Duration ?? plan.Metadata.Duration,
            Chapters: artifact.Chapters ?? plan.Chapters,
            CoverFileName: coverFileName), ct);

        if (ErrorCodeOf(attach) is { } attachError)
        {
            logger.LogError("Recording provenance for {ExternalId} failed: {Code}", plan.ExternalId, attachError);
            await RollbackAsync(bookId, createdByUs, storedNothing: false, plan, ct);
            await library.SetBookStatusAsync(bookId, BookStatus.Failed, attach.Reply, CancellationToken.None);
            return AcquisitionResult.Failed(attachError, attach.Reply);
        }

        if (bookTextScheduler is not null && !string.IsNullOrWhiteSpace(staged))
        {
            await bookTextScheduler.ScheduleAsync(bookId, Path.GetFileName(staged), ct);
        }

        progress.Report(new AcquisitionProgress("done", 100, BookId: bookId));

        var book = DataOf(await library.GetBookAsync(bookId, ct)) as BookDto;
        logger.LogInformation(
            "Acquired {Provider}/{ExternalId} ({AssetId}) into book {BookId} ({Bytes} bytes).",
            plan.ProviderId, plan.ExternalId, plan.Asset.Id, bookId, storedBytes);

        return AcquisitionResult.Acquired(bookId, book, $"Imported into library: {book?.Title ?? plan.Metadata.Title}.");
    }

    /// <summary>
    /// Creates or matches the book the asset will belong to, and reports
    /// whether this acquisition created the row (and therefore owns rolling it
    /// back).
    /// </summary>
    private async Task<(Guid BookId, bool CreatedByUs, BookDto? Book)> CreateOrMatchAsync(
        ProviderRegistration provider,
        ProviderAcquisitionPlan plan,
        AcquisitionRequest request,
        string expectedType,
        CancellationToken ct)
    {
        // The user's edits, over what the source said. Merged here as well as in
        // AcquireAsync (which validates the title) because this is where the book
        // is actually built; the merge is pure, so the two always agree.
        var metadata = MergeMetadata(plan.Metadata, request.MetadataOverrides);

        var command = BuildCreateRequest(provider, plan, metadata, request, expectedType, edition: false);
        var result = await library.CreateOrMatchBookAsync(command, strictConfirmation: false, ct);

        if (ErrorCodeOf(result) is { } code)
            throw new AcquisitionException(code, result.Reply);

        var outcome = (LibraryCreateOrMatchResultDto)result.Data!;
        var bookId = outcome.BookId
            ?? throw new AcquisitionException(AcquisitionException.Conflict, result.Reply);

        var createdByUs = outcome.Outcome == "created";
        var book = outcome.Book;

        // Identifier matching is deliberately type-blind, so acquiring an ebook
        // can "match" a physical row that shares an ISBN. Attaching an EPUB to a
        // physical book would be a modelling error, so in that case create a
        // proper edition of the same work instead — a book of the right type,
        // grouped with the one we matched.
        if (!createdByUs && book is not null && !string.Equals(book.Type, expectedType, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Acquisition of {ExternalId} matched a {Matched} book; creating a {Expected} edition of the same work instead.",
                plan.ExternalId, book.Type, expectedType);

            var edition = BuildCreateRequest(provider, plan, metadata, request, expectedType, edition: true);
            var editionResult = await library.CreateOrMatchBookAsync(edition, strictConfirmation: false, ct);

            if (ErrorCodeOf(editionResult) is { } editionCode)
                throw new AcquisitionException(editionCode, editionResult.Reply);

            var editionOutcome = (LibraryCreateOrMatchResultDto)editionResult.Data!;
            bookId = editionOutcome.BookId
                ?? throw new AcquisitionException(AcquisitionException.Conflict, editionResult.Reply);

            createdByUs = editionOutcome.Outcome == "created";
            book = editionOutcome.Book;
        }

        return (bookId, createdByUs, book);
    }

    /// <summary>
    /// Overlay a user's edits onto what the source supplied.
    ///
    /// A null field keeps the source's value, so a client that sends only what
    /// the user actually touched cannot flatten a later provider-side
    /// correction. An empty string clears the field — the user emptied that box,
    /// and restoring the source's text would be worse than dropping it. Title is
    /// the exception: a book with no title is not a book, so it falls back to
    /// the source and the caller's validation still applies.
    ///
    /// Identity is deliberately NOT merged. Provenance, and the "do I already
    /// have this work?" probe that runs before any download, both read the
    /// SOURCE metadata: an item is the same item however the user relabels it,
    /// and matching on the user's title would let a rename produce a second copy
    /// of a book they already own.
    /// </summary>
    private static ProviderMetadata MergeMetadata(
        ProviderMetadata source,
        ProviderMetadataOverrides? overrides)
    {
        if (overrides is null)
            return source;

        return source with
        {
            Title = Merge(source.Title, overrides.Title) ?? source.Title,
            Subtitle = Merge(source.Subtitle, overrides.Subtitle),
            Author = Merge(source.Author, overrides.Author),
            Description = Merge(source.Description, overrides.Description),
            Language = Merge(source.Language, overrides.Language),
            Publisher = Merge(source.Publisher, overrides.Publisher),
            PublishedDate = Merge(source.PublishedDate, overrides.PublishedDate),
            Categories = Merge(source.Categories, overrides.Categories),
            Narrator = Merge(source.Narrator, overrides.Narrator),
            Duration = Merge(source.Duration, overrides.Duration),
            PageCount = overrides.PageCount ?? source.PageCount,
        };

        static string? Merge(string? fromSource, string? fromUser) =>
            fromUser is null ? fromSource
            : string.IsNullOrWhiteSpace(fromUser) ? null
            : fromUser.Trim();
    }

    private static LibraryCreateBookRequest BuildCreateRequest(
        ProviderRegistration provider,
        ProviderAcquisitionPlan plan,
        ProviderMetadata metadata,
        AcquisitionRequest request,
        string expectedType,
        bool edition)
    {
        return new LibraryCreateBookRequest(
            ClientId: "acquisition",
            IdempotencyKey: $"acquire-{plan.ProviderId}-{plan.ExternalId}-{plan.Asset.Id}{(edition ? "-edition" : string.Empty)}-{Guid.NewGuid():N}",
            Type: expectedType,
            Title: metadata.Title,
            Subtitle: metadata.Subtitle,
            Author: metadata.Author,
            Narrator: metadata.Narrator,
            Description: metadata.Description,
            // No ISBN/ASIN: a provider's identifiers do not necessarily map to
            // the library's, and inventing one would corrupt identity matching.
            Duration: metadata.Duration,
            Publisher: metadata.Publisher,
            PublishedDate: metadata.PublishedDate,
            PageCount: metadata.PageCount,
            Language: metadata.Language,
            Categories: metadata.Categories,
            CollectionIds: request.CollectionIds,
            // An edition is created without consulting title/author matching, so
            // it lands as a new book in the work the match already identified.
            ForceCreate: edition);
    }

    private async Task RollbackAsync(
        Guid bookId,
        bool createdByUs,
        bool storedNothing,
        ProviderAcquisitionPlan plan,
        CancellationToken ct)
    {
        if (!storedNothing)
        {
            try
            {
                // Only the primary file: a matched book may have had a cover
                // before this acquisition, and rollback must not take it with it.
                await storage.DeleteBookFileAsync(bookId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Could not remove the stored file for book {BookId} during rollback; exception type {ExceptionType}. Details suppressed.",
                    bookId,
                    ex.GetType().Name);
            }
        }

        // Deliberately DO NOT delete the book row on failure:
        // A failed import must stay visible as Failed so the user can see and retry it.
        // Hard-deletion is reserved for explicit cancellation (handled in AcquireAsync).
    }

    /// <summary>
    /// Stores the source's cover and returns the bare file name storage produced,
    /// so the caller can record it on the book. Null when there is no cover, or
    /// when storing one failed: cover art must never cost the user the book.
    /// </summary>
    private async Task<string?> TryAttachCoverAsync(
        Guid bookId,
        byte[]? cover,
        ProviderAcquisitionPlan plan,
        CancellationToken ct)
    {
        if (cover is null || cover.Length == 0)
            return null;

        try
        {
            // The source's declared extension decides the served content type
            // and the stored name, so it is validated rather than trusted.
            var extension = NormalizeExtension(plan.Cover?.FileExtension ?? ".jpg");

            await using var stream = new MemoryStream(cover);
            var path = await storage.SaveBookCoverAsync(bookId, stream, $"cover{extension}", ct);

            var fileName = Path.GetFileName(path);
            logger.LogDebug("Attached cover {Cover} to book {BookId}.", fileName, bookId);
            return fileName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Storing cover art for book {BookId} failed; the book itself is unaffected. Exception type {ExceptionType}; details suppressed.",
                bookId,
                ex.GetType().Name);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<AcquisitionResult> AlreadyAcquiredAsync(
        Guid bookId,
        string providerId,
        ProgressSink progress,
        CancellationToken ct)
    {
        progress.Report(new AcquisitionProgress("done", 100, "already imported"));

        var result = await library.GetBookAsync(bookId, ct);
        var book = DataOf(result) as BookDto;

        logger.LogInformation("Acquisition skipped: {Provider} item is already in book {BookId}.", providerId, bookId);

        return AcquisitionResult.AlreadyAcquired(
            bookId, book, "This item is already in your library; nothing was downloaded.");
    }

    /// <summary>
    /// Read-only check for "the library already has a local file for this work".
    /// Only an unambiguous answer short-circuits the import; everything else
    /// falls through to the authoritative create-or-match at commit time.
    /// </summary>
    private async Task<Guid?> AlreadyHasLocalFileAsync(
        ProviderAcquisitionPlan plan,
        string expectedType,
        CancellationToken ct)
    {
        var resolved = await library.ResolveBookAsync(new LibraryResolveBookRequest(
            Title: plan.Metadata.Title,
            Author: plan.Metadata.Author,
            IncludeExternalMetadata: false), ct);

        if (resolved.Resolution != LibraryResolution.ExactMatch || resolved.MatchedBook is null)
            return null;

        var book = resolved.MatchedBook;
        return book.HasFile && string.Equals(book.Type, expectedType, StringComparison.Ordinal)
            ? book.Id
            : null;
    }

    private async Task<Guid?> FindProvenanceAsync(
        string providerId,
        string externalId,
        string assetId,
        CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        var bookId = await db.BookAcquisitions
            .AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.ExternalId == externalId && a.AssetId == assetId)
            .Select(a => (Guid?)a.BookId)
            .SingleOrDefaultAsync(ct);

        return bookId;
    }

    private static long EstimatedBytes(ProviderAcquisitionPlan plan)
    {
        var declared = plan.Parts.Sum(p => p.ExpectedBytes ?? 0);
        // The assembled result is roughly the size of its parts again, and the
        // answer must be conservative rather than optimistic.
        return declared > 0 ? declared * 2 : 0;
    }

    private void EnsureFreeSpace(string workingRoot, long expectedBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(workingRoot));
        if (string.IsNullOrEmpty(root))
            return;

        var required = Math.Max(_options.MinimumFreeSpaceBytes, (long)(expectedBytes * _options.FreeSpaceFactor));

        long available;
        try
        {
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell: better to try and fail on write than to refuse an
            // import for a reason we could not actually establish.
            logger.LogDebug(
                "Could not read free space for acquisition staging; exception type {ExceptionType}. Details suppressed.",
                ex.GetType().Name);
            return;
        }

        if (available < required)
            throw new AcquisitionException(
                AcquisitionException.InsufficientSpace,
                $"This import needs about {required / (1024 * 1024)} MB free but only {available / (1024 * 1024)} MB is available.");
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim();
        if (!value.StartsWith('.'))
            value = "." + value;

        if (!value.StartsWith('.') || value.Length is < 2 or > 8 || value.Contains('/') || value.Contains('\\'))
            throw new AcquisitionException(
                AcquisitionException.InvalidRequest, $"'{extension}' is not a usable file extension.");

        return value.ToLowerInvariant();
    }

    private static string? ErrorCodeOf(LibraryCommandResultDto envelope) => envelope.Data switch
    {
        LibraryErrorDto error => error.Code,
        LibraryConfirmationErrorDto confirmation => confirmation.Code,
        _ => null,
    };

    private static object? DataOf(LibraryCommandResultDto envelope) =>
        ErrorCodeOf(envelope) is null ? envelope.Data : null;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort. A stale staging directory is swept on the next run
            // rather than being allowed to fail the acquisition.
        }
    }

    /// <summary>
    /// Serialises progress callbacks: parts download concurrently, and an
    /// arbitrary <see cref="IProgress{T}"/> is not required to be thread-safe.
    /// </summary>
    private sealed class ProgressSink(IProgress<AcquisitionProgress>? inner)
    {
        private readonly object _lock = new();

        public void Report(AcquisitionProgress progress)
        {
            if (inner is null)
                return;

            lock (_lock)
                inner.Report(progress);
        }

        public void ReportDownload(int partIndex, long written, IReadOnlyList<ProviderDownloadPart> parts)
        {
            if (inner is null)
                return;

            var declared = parts.Sum(p => p.ExpectedBytes ?? 0);
            if (declared <= 0)
                return;

            lock (_lock)
            {
                var known = parts.Take(partIndex).Sum(p => p.ExpectedBytes ?? 0) + written;
                var fraction = Math.Min(1.0, (double)known / declared);
                inner.Report(new AcquisitionProgress("downloading", Math.Clamp(10 + (int)(fraction * 55), 10, 65)));
            }
        }
    }
}
