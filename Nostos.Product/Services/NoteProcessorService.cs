using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services;

public class NoteProcessorService
{
    private readonly ITopicRepository _topicRepo;

    public NoteProcessorService(ITopicRepository topicRepo)
    {
        _topicRepo = topicRepo;
    }

    /// <summary>
    /// Parses the note content for [[Topics]], creates them if missing,
    /// and updates the Many-to-Many links.
    ///
    /// A link whose name the note's stored text never declared (older data, an
    /// import) is kept, and written into the text so the text is complete; one
    /// named in <paramref name="removed"/> is the caller unlinking it, and goes.
    /// </summary>
    public async Task ProcessNoteAsync(NoteModel note, IReadOnlyCollection<string>? removed = null)
    {
        // A. Clear existing links for this note (Full refresh strategy), keeping
        //    what only the links knew.
        var linkOnly = await _topicRepo.ClearNoteLinksAsync(note);
        if (linkOnly.Count != 0)
        {
            var dropped = (removed ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            note.Content = TopicReferences.AppendAll(note.Content, linkOnly.Where(name => !dropped.Contains(name)));
        }

        // B. Parse content (the one parser every [[ ]] rule shares)
        var foundNames = TopicReferences.Names(note.Content).ToList();

        if (foundNames.Count == 0)
            return;

        // C. Find existing topics in DB to reuse
        var existingTopics = await _topicRepo.GetByNamesAsync(foundNames);

        var existingNames = existingTopics
            .Select(c => c.Topic)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // D. Create new topics
        var newTopics = foundNames
            .Where(name => !existingNames.Contains(name))
            .Select(name => new TopicModel { Topic = name })
            .ToList();

        if (newTopics.Count != 0)
        {
            _topicRepo.AddRange(newTopics);
        }

        // E. Create Links
        var allRelevantTopics = existingTopics.Concat(newTopics);

        foreach (var topic in allRelevantTopics)
        {
            _topicRepo.AddNoteLink(new NoteTopicModel { Note = note, Topic = topic });
        }
    }
}
