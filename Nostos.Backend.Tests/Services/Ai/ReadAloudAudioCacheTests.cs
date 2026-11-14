using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// The read-aloud cache is bounded but still large (512 MiB by default), so its
/// default placement is the host's product scratch root rather than a RAM-backed
/// /tmp (issue #1596). An explicit ReadAloud:CacheDirectory still wins.
/// </summary>
public sealed class ReadAloudAudioCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-read-aloud-cache-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public async Task The_default_cache_lives_on_the_host_scratch_root()
    {
        var scratch = new RecordingProductScratchStorage(Path.Combine(_root, "scratch"));
        var cache = new ReadAloudAudioCache(
            new ReadAloudOptions { CacheMaxBytes = 1024 * 1024 },
            NullLogger<ReadAloudAudioCache>.Instance,
            scratch);
        var audio = new ReadAloudAudio("rendered audio"u8.ToArray(), "audio/mpeg");
        var key = ReadAloudAudioCache.Key("http://speech:8880", "tts", "alloy", "A sentence.");

        await cache.SetAsync(key, audio);

        var cacheDirectory = Path.Combine(scratch.Root, "nostos-read-aloud-cache");
        Directory.Exists(cacheDirectory).Should().BeTrue();
        Directory.GetFiles(cacheDirectory).Should().ContainSingle()
            .Which.Should().StartWith(scratch.Root);

        var hit = await cache.TryGetAsync(key);
        hit.Should().NotBeNull();
        hit!.Audio.Should().Equal(audio.Audio);
    }

    [Fact]
    public async Task A_configured_cache_directory_still_wins()
    {
        var scratch = new RecordingProductScratchStorage(Path.Combine(_root, "scratch"));
        var configured = Path.Combine(_root, "configured-cache");
        var cache = new ReadAloudAudioCache(
            new ReadAloudOptions { CacheDirectory = configured, CacheMaxBytes = 1024 * 1024 },
            NullLogger<ReadAloudAudioCache>.Instance,
            scratch);
        var key = ReadAloudAudioCache.Key("http://speech:8880", "tts", "alloy", "A sentence.");

        await cache.SetAsync(key, new ReadAloudAudio("rendered audio"u8.ToArray(), "audio/mpeg"));

        Directory.Exists(configured).Should().BeTrue();
        Directory.Exists(scratch.Root).Should().BeFalse("an explicit directory replaces the seam's root");
    }
}
