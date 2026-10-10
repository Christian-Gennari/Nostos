using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Tests.Endpoints;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

/// <summary>
/// Exercises real alignment preflight validation through the HTTP endpoint.
///
/// No external transcription, ffmpeg invocation, or persistent library
/// storage is used. Every test uses the factory's temporary database
/// and storage root.
/// </summary>
public sealed class AlignmentPreflightTests
{
    [Fact]
    public async Task Source_without_media_is_a_typed_422()
    {
        using var factory = new LibraryEndpointFactory();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();

        using var host = AlignmentEndpointTests.CreateHost(
            factory, usage: usage);
        using var client = host.CreateClient();

        var (source, target) =
            await AlignmentEndpointTests.SeedEditionsAsync(
                client, factory, sameWork: true);

        await SetHasFileAsync(factory, source, false);

        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity);

        (await AlignmentEndpointTests.ProblemCodeAsync(response))
            .Should().Be("alignment_source_media_missing");

        usage.BeginCount.Should().Be(0);
        usage.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task Target_without_media_is_a_typed_422()
    {
        using var factory = new LibraryEndpointFactory();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();

        using var host = AlignmentEndpointTests.CreateHost(
            factory, usage: usage);
        using var client = host.CreateClient();

        var (source, target) =
            await AlignmentEndpointTests.SeedEditionsAsync(
                client, factory, sameWork: true);

        await SetHasFileAsync(factory, target, false);

        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity);

        (await AlignmentEndpointTests.ProblemCodeAsync(response))
            .Should().Be("alignment_target_not_epub");

        usage.BeginCount.Should().Be(0);
        usage.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_stored_epub_is_typed_and_never_reserves_usage()
    {
        using var factory = new LibraryEndpointFactory();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();

        using var host = AlignmentEndpointTests.CreateHost(
            factory, usage: usage);
        using var client = host.CreateClient();

        var (source, target) =
            await AlignmentEndpointTests.SeedEditionsAsync(
                client, factory, sameWork: true);

        // Database metadata advertises an EPUB, but no corresponding
        // file exists in the factory's isolated storage root.
        //
        // With the default self-hosted STT configuration unavailable,
        // the service may reject transcription availability first.
        // Both are preflight failures that must not reserve usage.
        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable);

        (await AlignmentEndpointTests.ProblemCodeAsync(response))
            .Should().Be("alignment_transcription_unavailable");

        usage.BeginCount.Should().Be(0);
        usage.CompleteCount.Should().Be(0);
    }

    private static async Task SetHasFileAsync(
        LibraryEndpointFactory factory,
        Guid bookId,
        bool hasFile)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={factory.DatabasePath}")
            .Options;

        await using var db = new NostosDbContext(options);

        var book = await db.Books.SingleAsync(
            candidate => candidate.Id == bookId);

        book.FileDetails.HasFile = hasFile;
        await db.SaveChangesAsync();
    }
}
