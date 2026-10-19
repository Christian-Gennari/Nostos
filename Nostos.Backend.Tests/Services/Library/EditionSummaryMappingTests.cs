using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// The book page's edition switcher (#1086) tells two editions of one format
/// apart by edition, year or publisher, so the summary must carry all three.
/// </summary>
public sealed class EditionSummaryMappingTests
{
    [Fact]
    public void Edition_summary_carries_the_details_that_tell_editions_apart()
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Dune", Author = "Frank Herbert" };
        book.Metadata.Edition = "2nd";
        book.Metadata.Publisher = "Ace";
        book.Metadata.PublishedDate = "1990-09-01";

        var summary = MappingExtensions.ToEditionSummary(book);

        summary.Edition.Should().Be("2nd");
        summary.Publisher.Should().Be("Ace");
        summary.PublishedDate.Should().Be("1990-09-01");
    }

    [Fact]
    public void Edition_summary_leaves_missing_details_null()
    {
        var summary = MappingExtensions.ToEditionSummary(new EBookModel { Title = "Dune" });

        summary.Publisher.Should().BeNull();
        summary.PublishedDate.Should().BeNull();
    }
}
