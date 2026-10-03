using System.Text.RegularExpressions;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services;

public partial class NoteProcessorService
{
    private readonly ITopicRepository _topicRepo;

    public NoteProcessorService(ITopicRepository topicRepo)
    {
        _topicRepo = topicRepo;
    }

    // 1. Encapsulate the Regex here
    [GeneratedRegex(@"\[\[(.*?)\]\]", RegexOptions.Compiled)]
    private static partial Regex TopicRegex();

    /// <summary>
    /// Parses the note content for [[Topics]], creates them if missing,
    /// and updates the Many-to-Many links.
    /// </summary>
    public async Task ProcessNoteAsync(NoteModel note)
    {
        // A. Parse content
        var matches = TopicRegex().Matches(note.Content);

        var foundNames = matches
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // B. Clear existing links for this note (Full refresh strategy)
        await _topicRepo.ClearNoteLinksAsync(note.Id);

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
