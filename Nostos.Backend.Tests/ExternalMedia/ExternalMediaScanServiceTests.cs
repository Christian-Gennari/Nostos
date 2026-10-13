using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaScanServiceTests : IDisposable
{
    private readonly ExternalMediaTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private ExternalMediaScanService Service(ILibraryMaintenanceCoordinator? maintenance = null) =>
        new(
            _bed.Root,
            new ExternalMediaDescriber(_bed.Root),
            _bed.Contexts,
            NullLogger<ExternalMediaScanService>.Instance,
            maintenance);

    [Fact]
    public async Task A_scan_waits_while_the_library_is_being_restored_or_switched()
    {
        File.WriteAllBytes(Path.Combine(_bed.ExternalRoot, "paper.pdf"), SyntheticMedia.Pdf());
        var maintenance = new LibraryMaintenanceCoordinator();
        var scans = Service(maintenance);
        var exclusive = await maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.BackupRestore);

        scans.TryStart(CancellationToken.None).Should().BeTrue();
        await Task.Delay(300);

        scans.Completion.IsCompleted.Should().BeFalse();
        scans.State.Result.Should().BeNull();

        await exclusive.DisposeAsync();
        await scans.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        scans.State.Status.Should().Be(ExternalMediaScanState.Done);
        scans.State.Result!.Summary.Ready.Should().Be(1);
    }

    [Theory]
    [InlineData("The Essay", "Sam Writer", "review")]
    [InlineData("the essay!", "SAM WRITER", "review")]
    [InlineData("The Essay", null, "review")]
    [InlineData("The Essay", "Someone Else", "ready")]
    [InlineData("Another Essay", "Sam Writer", "ready")]
    public async Task A_candidate_is_compared_with_the_library_by_title_and_by_author_when_both_name_one(
        string libraryTitle, string? libraryAuthor, string expected)
    {
        File.WriteAllBytes(Path.Combine(_bed.ExternalRoot, "essay.epub"), SyntheticMedia.Epub("The Essay", "Sam Writer"));
        await using (var db = await _bed.Contexts.CreateDbContextAsync())
        {
            db.Books.Add(new EBookModel { Title = libraryTitle, Author = libraryAuthor });
            await db.SaveChangesAsync();
        }

        var result = await Service().ScanAsync(CancellationToken.None);

        result.Items.Should().ContainSingle().Which.Status.Should().Be(expected);
    }

    [Fact]
    public async Task Many_candidates_are_matched_against_a_large_library_without_reloading_it_per_candidate()
    {
        for (var i = 0; i < 400; i++)
            File.WriteAllBytes(Path.Combine(_bed.ExternalRoot, $"Paper {i}.pdf"), SyntheticMedia.Pdf());
        await using (var db = await _bed.Contexts.CreateDbContextAsync())
        {
            for (var i = 0; i < 2000; i++)
                db.Books.Add(new EBookModel { Title = $"Library Book {i}", Author = "Author" });
            db.Books.Add(new EBookModel { Title = "Paper 7", Author = "Author" });
            await db.SaveChangesAsync();
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await Service().ScanAsync(CancellationToken.None);
        started.Stop();

        result.Summary.Discovered.Should().Be(400);
        result.Summary.NeedsReview.Should().Be(1);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_relative_folder_setting_stops_the_server_at_startup()
    {
        using var factory = new RelativeRootFactory();

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ExternalMedia:Root*absolute path*");
    }

    private sealed class RelativeRootFactory : LibraryEndpointFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ExternalMedia:Root", "media");
        }
    }
}
