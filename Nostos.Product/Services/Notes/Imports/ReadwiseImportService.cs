namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// "Import now" for a connected Readwise account (issue #1525): fetch the
/// export from the saved watermark, map it into the import's own shape, then
/// run the existing preview → match → commit flow. The watermark advances only
/// after the commit succeeds, so an abandoned or failed import fetches the
/// same changes next time.
/// </summary>
public sealed class ReadwiseImportService
{
    /// <summary>What the import names this source in the dialog and batch history.</summary>
    public const string DisplayName = "Readwise";

    private readonly ReadwiseConnectionService _connection;
    private readonly ReadwiseApiClient _api;
    private readonly HighlightImportService _imports;

    public ReadwiseImportService(
        ReadwiseConnectionService connection,
        ReadwiseApiClient api,
        HighlightImportService imports)
    {
        _connection = connection;
        _api = api;
        _imports = imports;
    }

    /// <summary>Reads the export and proposes where every book goes. Writes nothing.</summary>
    public async Task<HighlightImportPreview> PreviewAsync(CancellationToken ct = default)
    {
        var (data, _) = await FetchAsync(ct);
        return await _imports.PreviewSourceAsync(Source(data), ct);
    }

    /// <summary>
    /// Imports exactly the books the owner decided on, as one undo batch, then
    /// remembers that this run reached Readwise. Re-fetches the same range as
    /// the preview: the watermark is still the previous one.
    /// </summary>
    public async Task<HighlightImportResult> CommitAsync(
        IReadOnlyList<HighlightImportDecision>? decisions,
        Guid? batchId = null,
        CancellationToken ct = default)
    {
        var (data, startedAtUtc) = await FetchAsync(ct);
        var result = await _imports.CommitSourceAsync(
            Source(data),
            DisplayName,
            decisions ?? [],
            batchId,
            ct);
        await _connection.MarkImportedAsync(startedAtUtc, ct);
        return result;
    }

    private async Task<(ReadwiseExportData Data, DateTime StartedAtUtc)> FetchAsync(CancellationToken ct)
    {
        var status = await _connection.GetStatusAsync(ct);
        if (!status.Connected)
            throw new ReadwiseNotConnectedException();

        var token = await _connection.GetTokenAsync(ct);
        // Stamped before the first request: a highlight changed while the
        // export is running is fetched again rather than skipped.
        var startedAtUtc = DateTime.UtcNow;
        var data = await _api.ExportAsync(token, status.ImportedThroughUtc, ct);
        return (data, startedAtUtc);
    }

    private static ImportSourceFile Source(ReadwiseExportData data) =>
        new(HighlightImportSourceReader.Readwise, data.Books, data.Skipped);
}
