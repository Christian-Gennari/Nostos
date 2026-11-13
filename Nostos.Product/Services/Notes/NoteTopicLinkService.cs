using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Batch topic linking and the review flow's single-topic link (issue #1619):
/// every change rewrites each note's body with the <c>[[Topic]]</c> reference,
/// re-derives its links, and saves all of it at once. The links never bypass
/// <see cref="INoteRepository"/> or <see cref="ITopicRepository"/>.
/// </summary>
public sealed class NoteTopicLinkService
{
    private const int MaxTopicsPerLink = 20;
    private const int MaxNotesPerLink = 200;

    private readonly INoteRepository _notes;
    private readonly ITopicRepository _topics;
    private readonly NoteProcessorService _processor;
    private readonly NoteReadService _reads;

    public NoteTopicLinkService(
        INoteRepository notes,
        ITopicRepository topics,
        NoteProcessorService processor,
        NoteReadService reads)
    {
        _notes = notes;
        _topics = topics;
        _processor = processor;
        _reads = reads;
    }

    public Task<NoteCommandResult<TopicLinkResultDto>> LinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default) =>
        ChangeTopicReferencesAsync(topics, noteIds, TopicReferences.Append, unlink: false, ct);

    public Task<NoteCommandResult<TopicLinkResultDto>> UnlinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default) =>
        ChangeTopicReferencesAsync(topics, noteIds, TopicReferences.Remove, unlink: true, ct);

    /// <summary>
    /// Rewrites each listed note's body with <paramref name="change"/> for every
    /// named topic, re-derives its links, and saves all of it at once: either
    /// every note changes or none does.
    /// </summary>
    private async Task<NoteCommandResult<TopicLinkResultDto>> ChangeTopicReferencesAsync(
        IReadOnlyList<string>? topics,
        IReadOnlyList<Guid>? noteIds,
        Func<string, string, string> change,
        bool unlink,
        CancellationToken ct)
    {
        var names = (topics ?? [])
            .Select(name => name?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ids = (noteIds ?? []).Distinct().ToList();

        if (names.Count == 0 || ids.Count == 0)
            return InvalidTopicLink("Name at least one topic and one note.");
        if (names.Count > MaxTopicsPerLink || ids.Count > MaxNotesPerLink)
            return InvalidTopicLink($"At most {MaxTopicsPerLink} topics and {MaxNotesPerLink} notes at once.");
        if (!names.All(TopicReferences.IsValidName))
            return InvalidTopicLink(TopicReferences.InvalidNameMessage);

        var notes = await _notes.GetManyWithTopicsAsync(ids, ct);
        if (notes.Count != ids.Count)
            return NoteCommandResult<TopicLinkResultDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        var byId = notes.ToDictionary(n => n.Id);
        var ordered = ids.Select(id => byId[id]).ToList();
        var changed = new List<Guid>();
        foreach (var note in ordered)
        {
            var content = names.Aggregate(note.Content, change);
            // A link-only topic (no reference in the text) changes the links,
            // not the text, so the links are compared too.
            var linked = note.NoteTopics
                .Select(link => link.Topic?.Topic)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var linksChange = unlink ? names.Any(linked.Contains) : names.Any(name => !linked.Contains(name));
            if (content == note.Content && !linksChange) continue;

            note.Content = content;
            await _processor.ProcessNoteAsync(note, unlink ? names : null, ct: ct);
            changed.Add(note.Id);
        }

        if (changed.Count > 0)
            await _notes.SaveChangesAsync(ct);

        // Read back rather than mapped from the tracked notes, which hold no book.
        return NoteCommandResult<TopicLinkResultDto>.Ok(
            new TopicLinkResultDto(changed, await _reads.GetManyAsync(ids, ct)));
    }

    /// <summary>
    /// Links one note to an existing topic (issue #261): the target must already
    /// exist, the call is idempotent, and the note body stays the canonical
    /// source of membership on every later edit.
    /// </summary>
    public async Task<NoteCommandResult<NoteDto>> LinkToExistingTopicAsync(
        Guid noteId,
        Guid topicId,
        CancellationToken ct = default)
    {
        var note = await _notes.GetByIdWithTopicsAsync(noteId, ct);
        if (note is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        // Never creates a topic: the link target must already exist.
        var topic = await _topics.GetByIdAsync(topicId, ct);
        if (topic is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.TopicNotFound, "Topic not found.");

        // Idempotent. The join is keyed (NoteId, TopicId); inserting a second
        // row would be a duplicate key. The check runs against the database, not
        // the tracked navigation, so a repeated call sees the committed link.
        var existingNames = await _notes.GetTopicLinksAsync(noteId, ct);
        var alreadyLinked = existingNames.Any(link => link.TopicId == topicId);

        // The note body is the canonical source of membership on every later
        // edit. A join-only Act link would disappear when UpdateAsync reprocesses
        // that body. Normalize older join-only links on this note as well, so
        // accepting a new proposal does not make those disappear on the next edit.
        var before = note.Content;
        note.Content = TopicReferences.AppendAll(note.Content, existingNames.Select(link => link.Name).Append(topic.Topic));
        var contentChanged = note.Content != before;

        if (!alreadyLinked)
        {
            // Keep other links and save the text and join in one unit of work.
            _topics.AddNoteLink(new NoteTopicModel { NoteId = noteId, TopicId = topicId });
        }
        if (!alreadyLinked || contentChanged)
            await _notes.SaveChangesAsync(ct);

        return NoteCommandResult<NoteDto>.Ok(await _reads.SavedNoteAsync(noteId, ct));
    }

    private static NoteCommandResult<TopicLinkResultDto> InvalidTopicLink(string message) =>
        NoteCommandResult<TopicLinkResultDto>.Fail(NoteErrorCodes.InvalidTopicLink, message);
}
