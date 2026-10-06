using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.LibriVox;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class LibriVoxM4bAssemblerTests
{
    private sealed class FakeMediaProcessRunner : IMediaProcessRunner
    {
        public MediaToolAvailability Availability { get; set; } =
            new(true, "/usr/bin/ffmpeg", "/usr/bin/ffprobe", null);

        public Func<string, CancellationToken, Task<TimeSpan?>>? OnProbeDuration { get; set; }
        public Func<string, CancellationToken, Task<int?>>? OnProbeChapterCount { get; set; }
        public Func<IReadOnlyList<string>, CancellationToken, Task>? OnRunFfmpeg { get; set; }

        public List<IReadOnlyList<string>> RecordedFfmpegCalls { get; } = [];

        public Task<TimeSpan?> ProbeDurationAsync(string path, CancellationToken ct) =>
            OnProbeDuration != null
                ? OnProbeDuration(path, ct)
                : Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(10));

        public Task<int?> ProbeChapterCountAsync(string path, CancellationToken ct) =>
            OnProbeChapterCount != null
                ? OnProbeChapterCount(path, ct)
                : Task.FromResult<int?>(1);

        public async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            RecordedFfmpegCalls.Add(arguments);
            if (OnRunFfmpeg != null)
            {
                await OnRunFfmpeg(arguments, ct);
            }
        }
    }

    private static ProviderAcquisitionPlan CreatePlan(params (string Url, string Label)[] parts)
    {
        return new ProviderAcquisitionPlan(
            ProviderId: "librivox",
            ExternalId: "1234",
            Asset: new ProviderAsset("m4b", ProviderMediaKind.Audiobook, "M4B", "librivox-mp3-sections", null, true),
            Metadata: new ProviderMetadata("Test Book", "Test Author", null, "English", "2020", null, null, null),
            Parts: parts.Select(p => new ProviderDownloadPart(new Uri(p.Url), ".mp3", null, p.Label)).ToList(),
            Output: new ProviderOutput(".m4b", "audio/mp4", "M4B audiobook"),
            Cover: null,
            Source: new ProviderSourceInfo("https://librivox.org/test", null, null),
            Chapters: null);
    }

    [Fact]
    public async Task AssembleAsync_WhenUnavailable_ThrowsAcquisitionExceptionWithMediaToolMissing()
    {
        // 13. Availability reports unavailable -> the call throws AcquisitionException with Code "media_tool_missing"
        // and the availability's Problem message preserved.
        var fakeRunner = new FakeMediaProcessRunner
        {
            Availability = MediaToolAvailability.Missing("ffmpeg and ffprobe were not found on this system.")
        };

        var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var plan = CreatePlan(("https://archive.org/download/test/1.mp3", "Part 1"));
            var parts = new[] { new AcquisitionPart(Path.Combine(tempDir, "1.mp3"), ".mp3", 1000) };
            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);

            var act = () => assembler.AssembleAsync(context, CancellationToken.None);

            var ex = await act.Should().ThrowAsync<AcquisitionException>();
            ex.Which.Code.Should().Be(MediaToolException.NotInstalled);
            ex.Which.Message.Should().Be("ffmpeg and ffprobe were not found on this system.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssembleAsync_Success_ReturnsExpectedArtifactAndWritesCorrectMetadata()
    {
        // 14. Success: given a plan with 3 parts of measured durations (e.g. 10s, 5s, 7.5s),
        // the returned AcquisitionArtifact has FileExtension ".m4b", ContentType "audio/mp4",
        // exactly 3 Chapters whose Titles are the SECTION TITLES in order and whose start seconds are 0, 10, 15;
        // and Duration reflects the total measured.
        // Also assert the chapter metadata written to disk uses title= and TIMEBASE=1/1000 and contains NO CHAPTERTITLE,
        // and that the concat list lists the part paths in order.
        var fakeRunner = new FakeMediaProcessRunner();

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var part1Path = Path.Combine(tempDir, "track1.mp3");
            var part2Path = Path.Combine(tempDir, "track2.mp3");
            var part3Path = Path.Combine(tempDir, "track3.mp3");

            var durationMap = new Dictionary<string, TimeSpan>
            {
                [part1Path] = TimeSpan.FromSeconds(10),
                [part2Path] = TimeSpan.FromSeconds(5),
                [part3Path] = TimeSpan.FromSeconds(7.5),
                [Path.Combine(tempDir, LibriVoxM4bAssembler.OutputFileName)] = TimeSpan.FromSeconds(22.5)
            };

            fakeRunner.OnProbeDuration = (path, _) =>
            {
                if (durationMap.TryGetValue(path, out var d))
                    return Task.FromResult<TimeSpan?>(d);
                return Task.FromResult<TimeSpan?>(null);
            };

            fakeRunner.OnProbeChapterCount = (_, _) => Task.FromResult<int?>(3);

            var plan = CreatePlan(
                ("https://archive.org/download/test/1.mp3", "Chapter One"),
                ("https://archive.org/download/test/2.mp3", "Chapter Two"),
                ("https://archive.org/download/test/3.mp3", "Chapter Three"));

            var parts = new[]
            {
                new AcquisitionPart(part1Path, ".mp3", 1000),
                new AcquisitionPart(part2Path, ".mp3", 2000),
                new AcquisitionPart(part3Path, ".mp3", 3000)
            };

            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);
            var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);

            var artifact = await assembler.AssembleAsync(context, CancellationToken.None);

            artifact.FileExtension.Should().Be(".m4b");
            artifact.ContentType.Should().Be("audio/mp4");
            artifact.Duration.Should().Be("00:00:22"); // 22.5 seconds formatted as 00:00:22 (duration.Seconds is 22)
            artifact.Chapters.Should().NotBeNull();
            var chapters = artifact.Chapters!;
            chapters.Should().HaveCount(3);

            chapters[0].Title.Should().Be("Chapter One");
            chapters[0].StartTime.Should().Be(0.0);

            chapters[1].Title.Should().Be("Chapter Two");
            chapters[1].StartTime.Should().Be(10.0);

            chapters[2].Title.Should().Be("Chapter Three");
            chapters[2].StartTime.Should().Be(15.0);

            // Verify chapters file on disk
            var chaptersFile = Path.Combine(tempDir, LibriVoxM4bAssembler.ChaptersFileName);
            File.Exists(chaptersFile).Should().BeTrue();
            var chaptersContent = await File.ReadAllTextAsync(chaptersFile);

            chaptersContent.Should().Contain("TIMEBASE=1/1000");
            chaptersContent.Should().Contain("title=Chapter One");
            chaptersContent.Should().Contain("title=Chapter Two");
            chaptersContent.Should().Contain("title=Chapter Three");
            chaptersContent.Should().NotContain("CHAPTERTITLE");

            // Verify concat list on disk
            var concatFile = Path.Combine(tempDir, LibriVoxM4bAssembler.ConcatListFileName);
            File.Exists(concatFile).Should().BeTrue();
            var concatLines = (await File.ReadAllLinesAsync(concatFile))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            concatLines.Should().HaveCount(3);
            concatLines[0].Should().Be($"file '{part1Path}'");
            concatLines[1].Should().Be($"file '{part2Path}'");
            concatLines[2].Should().Be($"file '{part3Path}'");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssembleAsync_FfmpegArguments_ContainExpectedFlagsInOrder()
    {
        // 15. The ffmpeg argument list passed to the runner contains, in order:
        // "-f", "concat", "-safe", "0", "-i", <list path>, "-i", <chapters path>, "-map", "0:a",
        // "-map_metadata", "1", "-map_chapters", "1", "-c:a", "aac", "-b:a", "64k", "-ac", "1",
        // "-ar", "44100", "-movflags", "+faststart", "-f", "ipod", <output ending .m4b>.
        // Assert the presence of each of -ar 44100, -map_chapters 1, -map_metadata 1, -movflags +faststart, -f ipod explicitly.
        var fakeRunner = new FakeMediaProcessRunner();

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var partPath = Path.Combine(tempDir, "1.mp3");
            fakeRunner.OnProbeDuration = (_, _) => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(10));
            fakeRunner.OnProbeChapterCount = (_, _) => Task.FromResult<int?>(1);

            var plan = CreatePlan(("https://archive.org/download/test/1.mp3", "Part 1"));
            var parts = new[] { new AcquisitionPart(partPath, ".mp3", 1000) };
            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);

            var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);
            await assembler.AssembleAsync(context, CancellationToken.None);

            fakeRunner.RecordedFfmpegCalls.Should().HaveCount(1);
            var args = fakeRunner.RecordedFfmpegCalls[0];

            var listPath = Path.Combine(tempDir, LibriVoxM4bAssembler.ConcatListFileName);
            var chaptersPath = Path.Combine(tempDir, LibriVoxM4bAssembler.ChaptersFileName);
            var outputPath = Path.Combine(tempDir, LibriVoxM4bAssembler.OutputFileName);

            var expectedArgs = new[]
            {
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-i", chaptersPath,
                "-map", "0:a",
                "-map_metadata", "1",
                "-map_chapters", "1",
                "-c:a", "aac",
                "-aac_coder", "fast",
                "-b:a", "64k",
                "-ac", "1",
                "-ar", "44100",
                "-movflags", "+faststart",
                "-f", "ipod",
                outputPath
            };

            args.Should().Equal(expectedArgs);

            // Explicit presence checks for load-bearing flags
            args.Should().ContainInOrder("-ar", "44100");
            args.Should().ContainInOrder("-map_chapters", "1");
            args.Should().ContainInOrder("-map_metadata", "1");
            args.Should().ContainInOrder("-movflags", "+faststart");
            args.Should().ContainInOrder("-f", "ipod");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssembleAsync_ChapterTitleEscaping_EscapesSyntaxCharacters()
    {
        // 16. Chapter-title escaping: a section title containing '=', ';', '#', '\\'
        // (e.g. "Ch 1; Part=2 #3 \\ done") is escaped in the written metadata so the value is not truncated.
        var fakeRunner = new FakeMediaProcessRunner();

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var partPath = Path.Combine(tempDir, "1.mp3");
            fakeRunner.OnProbeDuration = (_, _) => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(10));
            fakeRunner.OnProbeChapterCount = (_, _) => Task.FromResult<int?>(1);

            var specialTitle = @"Ch 1; Part=2 #3 \ done";
            var plan = CreatePlan(("https://archive.org/download/test/1.mp3", specialTitle));
            var parts = new[] { new AcquisitionPart(partPath, ".mp3", 1000) };
            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);

            var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);
            await assembler.AssembleAsync(context, CancellationToken.None);

            var chaptersFile = Path.Combine(tempDir, LibriVoxM4bAssembler.ChaptersFileName);
            var content = await File.ReadAllTextAsync(chaptersFile);

            content.Should().Contain(@"title=Ch 1\; Part\=2 \#3 \\ done");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssembleAsync_SingleQuoteInPath_EscapesCorrectlyForConcatList()
    {
        // 17. A path containing a single quote does not break the concat list (the path is quoted the ffmpeg way).
        var fakeRunner = new FakeMediaProcessRunner();

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var partPath = Path.Combine(tempDir, "reader's track.mp3");
            fakeRunner.OnProbeDuration = (_, _) => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(10));
            fakeRunner.OnProbeChapterCount = (_, _) => Task.FromResult<int?>(1);

            var plan = CreatePlan(("https://archive.org/download/test/1.mp3", "Part 1"));
            var parts = new[] { new AcquisitionPart(partPath, ".mp3", 1000) };
            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);

            var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);
            await assembler.AssembleAsync(context, CancellationToken.None);

            var concatFile = Path.Combine(tempDir, LibriVoxM4bAssembler.ConcatListFileName);
            var content = await File.ReadAllTextAsync(concatFile);

            var escapedPath = partPath.Replace("'", @"'\''");
            content.Trim().Should().Be($"file '{escapedPath}'");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssembleAsync_ProbeChapterCountMismatch_ThrowsAcquisitionException()
    {
        // 18. ProbeChapterCountAsync returning a count different from the number of chapters ->
        // throws AcquisitionException (assembly failure), and no artifact is returned.
        var fakeRunner = new FakeMediaProcessRunner();

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var partPath = Path.Combine(tempDir, "1.mp3");
            fakeRunner.OnProbeDuration = (_, _) => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(10));
            // Return 0 chapters instead of 1 expected
            fakeRunner.OnProbeChapterCount = (_, _) => Task.FromResult<int?>(0);

            var plan = CreatePlan(("https://archive.org/download/test/1.mp3", "Part 1"));
            var parts = new[] { new AcquisitionPart(partPath, ".mp3", 1000) };
            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);

            var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);

            var act = () => assembler.AssembleAsync(context, CancellationToken.None);

            var ex = await act.Should().ThrowAsync<AcquisitionException>();
            ex.Which.Code.Should().Be(MediaToolException.Failed);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssembleAsync_ProducedDurationWildlyDifferent_ThrowsAcquisitionException()
    {
        // 19. A produced duration wildly different from the measured total -> throws AcquisitionException.
        var fakeRunner = new FakeMediaProcessRunner();

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var partPath = Path.Combine(tempDir, "1.mp3");
            var outputPath = Path.Combine(tempDir, LibriVoxM4bAssembler.OutputFileName);

            fakeRunner.OnProbeDuration = (path, _) =>
            {
                if (path == partPath)
                    return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(100));
                if (path == outputPath)
                    return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(10)); // 10s vs 100s total
                return Task.FromResult<TimeSpan?>(null);
            };
            fakeRunner.OnProbeChapterCount = (_, _) => Task.FromResult<int?>(1);

            var plan = CreatePlan(("https://archive.org/download/test/1.mp3", "Part 1"));
            var parts = new[] { new AcquisitionPart(partPath, ".mp3", 1000) };
            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);

            var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);

            var act = () => assembler.AssembleAsync(context, CancellationToken.None);

            var ex = await act.Should().ThrowAsync<AcquisitionException>();
            ex.Which.Code.Should().Be(MediaToolException.Failed);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssembleAsync_ZeroParts_ThrowsAcquisitionException()
    {
        // 20. Zero parts -> failure (does not silently produce an empty file).
        var fakeRunner = new FakeMediaProcessRunner();

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var plan = CreatePlan();
            var parts = Array.Empty<AcquisitionPart>();
            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);

            var assembler = new LibriVoxM4bAssembler(fakeRunner, NullLogger<LibriVoxM4bAssembler>.Instance);

            var act = () => assembler.AssembleAsync(context, CancellationToken.None);

            var ex = await act.Should().ThrowAsync<AcquisitionException>();
            ex.Which.Code.Should().Be(MediaToolException.Failed);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RealFfmpeg_EndToEnd_GeneratesAudioAndAssemblesM4b()
    {
        // 21. In a test that first checks ffmpeg/ffprobe are actually available on PATH
        // (use new MediaProcessRunner(Options.Create(new MediaToolOptions()), NullLogger<MediaProcessRunner>.Instance).Availability):
        // if available, generate 3 tiny MP3s ON THE FLY into a temp directory with ffmpeg
        // (e.g. ffmpeg -f lavfi -i anullsrc=r=22050:cl=mono -t 1 -c:a libmp3lame -b:a 64k out.mp3,
        // plus a second one at a DIFFERENT sample rate such as r=44100 to prove the resampling matters),
        // then run the REAL LibriVoxM4bAssembler against a plan of those 3 parts and assert the produced .m4b:
        // exists, ffprobe reports 3 chapters with the expected titles, the audio stream sample rate is 44100
        // and channels is 1, and the total duration is within a second of the sum of the inputs.
        // Use the real MediaProcessRunner for this test.
        // If ffmpeg is NOT available, this test must instead assert the documented media_tool_missing behaviour
        // so it is meaningful either way — and say clearly in a comment that ffmpeg is required for the full check.
        // Clean up the temp directory in a finally block.
        var realRunner = new MediaProcessRunner(
            Options.Create(new MediaToolOptions()),
            NullLogger<MediaProcessRunner>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), "nostos-ffmpeg-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var plan = CreatePlan(
                ("https://archive.org/download/test/1.mp3", "Chapter 1: Intro"),
                ("https://archive.org/download/test/2.mp3", "Chapter 2: Middle"),
                ("https://archive.org/download/test/3.mp3", "Chapter 3: End"));

            var mp31 = Path.Combine(tempDir, "section_1.mp3");
            var mp32 = Path.Combine(tempDir, "section_2.mp3");
            var mp33 = Path.Combine(tempDir, "section_3.mp3");

            if (!realRunner.Availability.Available)
            {
                // ffmpeg is required for the full end-to-end check.
                // If not installed on the system, verify that assembler rejects the call cleanly.
                var partsMissing = new[]
                {
                    new AcquisitionPart(mp31, ".mp3", 100),
                    new AcquisitionPart(mp32, ".mp3", 100),
                    new AcquisitionPart(mp33, ".mp3", 100)
                };
                var contextMissing = new AcquisitionAssemblyContext(plan, partsMissing, tempDir);
                var assemblerMissing = new LibriVoxM4bAssembler(realRunner, NullLogger<LibriVoxM4bAssembler>.Instance);

                var act = () => assemblerMissing.AssembleAsync(contextMissing, CancellationToken.None);
                var ex = await act.Should().ThrowAsync<AcquisitionException>();
                ex.Which.Code.Should().Be(MediaToolException.NotInstalled);
                return;
            }

            // Generate 3 tiny MP3s on the fly using ffmpeg
            // Track 1: 1s at 22050 Hz mono
            await realRunner.RunFfmpegAsync(
                ["-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "1", "-c:a", "libmp3lame", "-b:a", "64k", mp31],
                CancellationToken.None);

            // Track 2: 2s at 44100 Hz mono (different sample rate to prove uniform resampling to 44100)
            await realRunner.RunFfmpegAsync(
                ["-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", "2", "-c:a", "libmp3lame", "-b:a", "64k", mp32],
                CancellationToken.None);

            // Track 3: 1s at 22050 Hz mono
            await realRunner.RunFfmpegAsync(
                ["-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "1", "-c:a", "libmp3lame", "-b:a", "64k", mp33],
                CancellationToken.None);

            File.Exists(mp31).Should().BeTrue();
            File.Exists(mp32).Should().BeTrue();
            File.Exists(mp33).Should().BeTrue();

            var parts = new[]
            {
                new AcquisitionPart(mp31, ".mp3", new FileInfo(mp31).Length),
                new AcquisitionPart(mp32, ".mp3", new FileInfo(mp32).Length),
                new AcquisitionPart(mp33, ".mp3", new FileInfo(mp33).Length)
            };

            var context = new AcquisitionAssemblyContext(plan, parts, tempDir);
            var assembler = new LibriVoxM4bAssembler(realRunner, NullLogger<LibriVoxM4bAssembler>.Instance);

            var artifact = await assembler.AssembleAsync(context, CancellationToken.None);

            // Assert produced .m4b exists
            File.Exists(artifact.FilePath).Should().BeTrue();
            artifact.FileExtension.Should().Be(".m4b");
            artifact.ContentType.Should().Be("audio/mp4");

            // ffprobe reports 3 chapters with expected titles
            var chapterCount = await realRunner.ProbeChapterCountAsync(artifact.FilePath, CancellationToken.None);
            chapterCount.Should().Be(3);

            artifact.Chapters.Should().NotBeNull();
            var chapters = artifact.Chapters!;
            chapters.Should().HaveCount(3);
            chapters[0].Title.Should().Be("Chapter 1: Intro");
            chapters[1].Title.Should().Be("Chapter 2: Middle");
            chapters[2].Title.Should().Be("Chapter 3: End");

            // Total duration is within a second of sum of inputs (1 + 2 + 1 = 4 seconds)
            var producedDuration = await realRunner.ProbeDurationAsync(artifact.FilePath, CancellationToken.None);
            producedDuration.Should().NotBeNull();
            producedDuration!.Value.TotalSeconds.Should().BeInRange(3.0, 5.0);

            // Audio stream sample rate is 44100 and channels is 1
            var ffprobePath = realRunner.Availability.FfprobePath!;
            var probeStart = new ProcessStartInfo(ffprobePath)
            {
                Arguments = $"-v error -select_streams a:0 -show_entries stream=sample_rate,channels -of csv=p=0 \"{artifact.FilePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(probeStart);
            proc.Should().NotBeNull();
            var output = await proc!.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            proc.ExitCode.Should().Be(0);

            // Output format csv: "44100,1" or two lines depending on ffprobe csv format
            var tokens = output.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            tokens.Should().Contain("44100");
            tokens.Should().Contain("1");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
