using Microsoft.Extensions.Logging;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// The raw transcript and its lifecycle (issue #262 §7, §8; split out in issue
/// #1619): capture post-processing, re-processing from the original words,
/// Ask Nostos's "clean that up", reading the transcript, and restoring it. The
/// raw transcript is never overwritten by any of them.
/// </summary>
public sealed class NoteProcessingLifecycleService
{
    private readonly INoteRepository _notes;
    private readonly NoteProcessorService _processor;
    private readonly IThoughtProcessor _thoughts;
    private readonly NoteReadService _reads;
    private readonly ILogger<NoteProcessingLifecycleService> _logger;

    public NoteProcessingLifecycleService(
        INoteRepository notes,
        NoteProcessorService processor,
        IThoughtProcessor thoughts,
        NoteReadService reads,
        ILogger<NoteProcessingLifecycleService> logger)
    {
        _notes = notes;
        _processor = processor;
        _thoughts = thoughts;
        _reads = reads;
        _logger = logger;
    }

    public async Task<NoteCommandResult<NoteDto>> ReprocessAsync(
        Guid noteId,
        string processingMode,
        CancellationToken ct = default)
    {
        if (!ThoughtProcessingModes.IsSupported(processingMode))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.InvalidProcessingMode,
                $"Unknown processing mode '{processingMode}'.");
        }

        var note = await _notes.GetByIdWithTopicsAsync(noteId, ct);
        if (note is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        var mode = ThoughtProcessingModes.Normalize(processingMode);

        // The original transcript is the ONLY source of a re-run: a second pass
        // (say, clarify after light polish) must never transform transformed
        // prose. A note that predates the mode feature has no RawContent yet, so
        // its current text IS the original and becomes the raw transcript now.
        var raw = note.RawContent;
        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(note.Content))
        {
            raw = note.Content;
            note.RawContent = raw;
        }

        if (mode == ThoughtProcessingModes.Verbatim || string.IsNullOrWhiteSpace(raw))
        {
            // Verbatim restore (or a quote-only capture with no thought text):
            // the raw words are the stored words, and no provider is called.
            note.Content = raw ?? note.Content;
            note.ProcessingMode = ThoughtProcessingModes.Verbatim;
        }
        else
        {
            var result = await _thoughts.ProcessAsync(raw, mode, ct);
            note.Content = result.Text;
            note.ProcessingMode = result.Mode;
        }

        // Content may have changed, so the topic links are re-derived from it.
        await _processor.ProcessNoteAsync(note, ct: ct);
        await _notes.SaveChangesAsync(ct);

        return NoteCommandResult<NoteDto>.Ok(await _reads.SavedNoteAsync(noteId, ct));
    }

    public async Task<NoteCommandResult<NoteDto>> RewriteCaptureAsync(
        Guid noteId,
        string clientId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !await _notes.HasReceiptForNoteAsync(clientId, noteId, ct))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.NoteNotFound,
                "No note captured in this conversation has that id.");
        }

        return await ReprocessAsync(noteId, ThoughtProcessingModes.Clarify, ct);
    }

    public async Task<NoteCommandResult<NoteRawTranscriptDto>> GetRawAsync(
        Guid noteId,
        CancellationToken ct = default)
    {
        var note = await _notes.GetByIdAsync(noteId, ct);
        if (note is null)
            return NoteCommandResult<NoteRawTranscriptDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        return NoteCommandResult<NoteRawTranscriptDto>.Ok(
            new NoteRawTranscriptDto(note.Id, note.RawContent, note.Content, note.ProcessingMode));
    }

    public async Task<NoteCommandResult<NoteDto>> RestoreRawAsync(
        Guid noteId,
        CancellationToken ct = default)
    {
        var note = await _notes.GetByIdWithTopicsAsync(noteId, ct);
        if (note is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        if (string.IsNullOrWhiteSpace(note.RawContent))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.NoRawTranscript,
                "This note has no raw transcript to restore.");
        }

        // Restore the original words and record the honest mode. RawContent is
        // left exactly as it was — restoring is not a way to erase the capture.
        note.Content = note.RawContent;
        note.ProcessingMode = ThoughtProcessingModes.Verbatim;

        await _processor.ProcessNoteAsync(note, ct: ct);
        await _notes.SaveChangesAsync(ct);

        return NoteCommandResult<NoteDto>.Ok(await _reads.SavedNoteAsync(noteId, ct));
    }

    /// <summary>
    /// Applies the capture's mode to the model before it is stored. The raw
    /// transcript is preserved in <see cref="NoteModel.RawContent"/> and is never
    /// overwritten; a provider failure keeps the raw words rather than losing the
    /// thought.
    /// </summary>
    public async Task ApplyCaptureProcessingAsync(NoteModel model, string mode, CancellationToken ct = default)
    {
        var normalized = ThoughtProcessingModes.Normalize(mode);
        if (normalized == ThoughtProcessingModes.Verbatim)
            return;

        // A quote-only capture has no user thought to process. The quote lives in
        // SelectedText and is never handed to the processor, so it cannot be
        // rewritten by any mode. Nothing was processed, so the honest mode is
        // verbatim rather than the requested one.
        if (string.IsNullOrWhiteSpace(model.Content))
        {
            model.ProcessingMode = ThoughtProcessingModes.Verbatim;
            return;
        }

        // The raw transcript is the source of truth. A request that asks for a
        // processed mode but supplies no raw transcript means the incoming
        // Content IS the raw transcript — so it is kept before Content changes.
        model.RawContent ??= model.Content;

        try
        {
            var result = await _thoughts.ProcessAsync(model.RawContent, normalized, ct);
            model.Content = result.Text;
            model.ProcessingMode = result.Mode;
        }
        catch (LlmException ex)
        {
            // A thought is never lost to a provider failure: the raw transcript
            // becomes the content and the stored mode says so honestly.
            _logger.LogWarning(
                ex,
                "Post-processing capture {NoteId} in mode {Mode} failed; keeping the raw transcript.",
                model.Id,
                normalized);
            model.Content = model.RawContent;
            model.ProcessingMode = ThoughtProcessingModes.Verbatim;
        }
    }
}
