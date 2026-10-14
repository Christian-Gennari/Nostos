using System.Security.Cryptography;
using System.Text;
using Nostos.Backend.Configuration;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Rendered sentences on disk, so that going back a page or reading a chapter
/// again costs nothing the second time.
///
/// Files are named by a hash of everything that decides how a sentence sounds
/// (server, model, voice, text), so an entry can never be served for different
/// words. The cache is bounded: when it grows past the limit the files read
/// longest ago are removed. It lives outside the library's storage and losing
/// it loses nothing; every failure here is swallowed and the sentence is
/// simply rendered again.
/// </summary>
public sealed class ReadAloudAudioCache(ReadAloudOptions options, ILogger<ReadAloudAudioCache> logger)
{
    private const string Extension = ".audio";
    private readonly SemaphoreSlim trimGate = new(1, 1);
    private long bytesSinceTrim;

    private string Directory => string.IsNullOrWhiteSpace(options.CacheDirectory)
        ? Path.Combine(Path.GetTempPath(), "nostos-read-aloud-cache")
        : options.CacheDirectory;

    private bool Enabled => options.CacheMaxBytes > 0;

    public static string Key(string server, string model, string voice, string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{server}\n{model}\n{voice}\n{text}"));
        return Convert.ToHexStringLower(hash);
    }

    public async Task<ReadAloudAudio?> TryGetAsync(string key, CancellationToken ct = default)
    {
        if (!Enabled)
            return null;

        try
        {
            var path = PathFor(key);
            if (!File.Exists(path))
                return null;

            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length == 0)
                return null;

            // "Read longest ago" is what trimming removes, and many filesystems
            // do not keep access times, so the write time carries it.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return new ReadAloudAudio(bytes, "audio/mpeg");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Read-aloud cache read failed.");
            return null;
        }
    }

    public async Task SetAsync(string key, ReadAloudAudio audio, CancellationToken ct = default)
    {
        if (!Enabled || audio.Audio.Length == 0)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(key);
            var partial = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(partial, audio.Audio, ct);
            File.Move(partial, path, overwrite: true);

            // Listing the folder on every write would be the expensive part, so
            // trim once per ~16 MiB written.
            if (Interlocked.Add(ref bytesSinceTrim, audio.Audio.Length) > 16 * 1024 * 1024)
            {
                Interlocked.Exchange(ref bytesSinceTrim, 0);
                await TrimAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Read-aloud cache write failed.");
        }
    }

    /// <summary>Removes the files read longest ago until the cache fits its limit.</summary>
    public async Task TrimAsync()
    {
        if (!await trimGate.WaitAsync(0))
            return;

        try
        {
            var directory = new DirectoryInfo(Directory);
            if (!directory.Exists)
                return;

            var files = directory.GetFiles($"*{Extension}").OrderBy(file => file.LastWriteTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= options.CacheMaxBytes)
                    break;
                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Read-aloud cache trim failed.");
        }
        finally
        {
            trimGate.Release();
        }
    }

    private string PathFor(string key) => Path.Combine(Directory, $"{key}{Extension}");
}
