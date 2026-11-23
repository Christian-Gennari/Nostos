using FluentAssertions;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// The default <see cref="ITopicRepository.GetPageWithUsageAsync"/> a host or
/// test double inherits (issue #1770 review, round 2). It filters the list it
/// was given, so it must count the untyped topics before applying the kind
/// filter — otherwise a page for one kind claims the library has none.
/// </summary>
public sealed class TopicRepositoryPageDefaultTests
{
    [Fact]
    public async Task A_page_filtered_to_one_kind_still_reports_the_untyped_total()
    {
        var person = Guid.NewGuid();
        ITopicRepository repository = new ListOnlyTopics(
        [
            new TopicDto(Guid.NewGuid(), "Freire", 3, KindId: person, KindSource: TopicKindSources.Reader),
            new TopicDto(Guid.NewGuid(), "Montessori", 2),
            new TopicDto(Guid.NewGuid(), "Dewey", 1),
        ]);

        var page = await repository.GetPageWithUsageAsync(1, 50, unkindedOnly: false, kindId: person);

        page.Items.Should().ContainSingle().Which.Name.Should().Be("Freire");
        page.Total.Should().Be(1);
        page.UnkindedTotal.Should().Be(2, "two topics have no kind, before the kind filter");
    }

    [Fact]
    public async Task An_untyped_page_reports_the_untyped_total_as_its_total()
    {
        ITopicRepository repository = new ListOnlyTopics(
        [
            new TopicDto(Guid.NewGuid(), "Freire", 3, KindId: Guid.NewGuid(), KindSource: TopicKindSources.Reader),
            new TopicDto(Guid.NewGuid(), "Montessori", 2),
            new TopicDto(Guid.NewGuid(), "Dewey", 1),
        ]);

        var page = await repository.GetPageWithUsageAsync(1, 50, unkindedOnly: true, kindId: null);

        page.Items.Should().HaveCount(2);
        page.Total.Should().Be(2);
        page.UnkindedTotal.Should().Be(2);
    }

    /// <summary>
    /// A host that only implements the one abstract read inherits the paging
    /// default under test; every other member is unused here.
    /// </summary>
    private sealed class ListOnlyTopics(IReadOnlyList<TopicDto> topics) : ITopicRepository
    {
        public Task<List<TopicDto>> GetAllWithUsageCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(topics.ToList());

        public Task<List<TopicDto>> SearchByNoteTextAsync(string term, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<TopicDto>> SearchByNoteTextAsync(
            string term,
            IReadOnlyList<Guid>? bookIds,
            int limit = 50,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicStatsDto> GetStatsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<RelatedTopicDto>> GetRelatedAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicGraphDto> GetGraphAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicWritingUsageDto?> GetWritingUsageAsync(
            Guid id,
            int limit,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TopicModel?> GetByIdWithNotesAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicModel?> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicModel?> MergeAsync(Guid sourceId, Guid targetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicModel?> SetDescriptionAsync(
            Guid id,
            string? description,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<TopicModel>> GetByNamesAsync(
            IEnumerable<string> names,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void AddRange(IEnumerable<TopicModel> topics) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ClearNoteLinksAsync(
            NoteModel note,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void AddNoteLink(NoteTopicModel link) => throw new NotSupportedException();

        public Task<int> DeleteOrphanedAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> MergeCaseVariantsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<List<TopicKindDto>> GetAllKindsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicKindMutationResult> CreateKindAsync(
            string name,
            string colorKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicKindMutationResult> UpdateKindAsync(
            Guid id,
            string? name,
            string? colorKey,
            int? sortOrder,
            bool swapColor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TopicKindReorderResult> ReorderKindsAsync(
            IReadOnlyList<Guid>? ids,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteKindAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SetTopicsKindOutcome> SetTopicsKindAsync(
            IReadOnlyList<Guid> topicIds,
            Guid? kindId,
            string source = TopicKindSources.Reader,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SetTopicsKindOutcome> SetTopicKindsAsync(
            IReadOnlyList<TopicKindAssignment> assignments,
            string source = TopicKindSources.Reader,
            bool overwriteReaderKinds = true,
            bool ignoreUnknownTopicIds = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
