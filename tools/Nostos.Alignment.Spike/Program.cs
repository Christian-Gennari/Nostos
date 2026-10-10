using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Nostos.Alignment.Spike;
using Nostos.Backend.Services.Ai;
using Nostos.Product.BookText;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    switch (args.FirstOrDefault())
    {
        case "extract" when args.Length == 3:
        {
            using var source = File.OpenRead(args[1]);
            var extracted = await new EpubBookTextExtractor().ExtractAsync(source);
            var result = new ExtractedArtifact(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[1]))),
                BookTextArtifactSchema.CurrentExtractorVersion, extracted);
            await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(result, json));
            Console.WriteLine($"Extracted {extracted.Blocks.Count} blocks / {extracted.CharacterCount} characters.");
            break;
        }
        case "match" when args.Length == 5:
        {
            var artifact = JsonSerializer.Deserialize<ExtractedArtifact>(File.ReadAllText(args[1]), json)!;
            var settings = JsonSerializer.Deserialize<MatcherSettings>(File.ReadAllText(args[2]), json)!;
            var transcript = JsonSerializer.Deserialize<TranscriptArtifact>(File.ReadAllText(args[3]), json)!;
            var match = new PassageMatcher(artifact.Document).Locate(transcript.Result.Text, settings);
            await File.WriteAllTextAsync(args[4], JsonSerializer.Serialize(new { artifact.SourceSha256, artifact.ExtractorVersion, settings, transcript, match }, json));
            Console.WriteLine($"{match.Reason}; confidence={match.Score:F3}");
            break;
        }
        case "transcribe" when args.Length == 4:
        {
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[1]));
            var type = assembly.GetTypes().Single(x => !x.IsAbstract && typeof(ISTtProvider).IsAssignableFrom(x));
            var provider = (ISTtProvider)Activator.CreateInstance(type)!;
            try
            {
                using var audio = File.OpenRead(args[2]);
                var timer = Stopwatch.StartNew();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var result = await provider.TranscribeAsync(audio, Path.GetFileName(args[2]), "audio/wav", "en", timeout.Token);
                var output = new TranscriptArtifact(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[2]))),
                    DateTimeOffset.UtcNow, timer.Elapsed.TotalMilliseconds, result);
                await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(output, json));
                Console.WriteLine("Transcription recorded.");
            }
            finally { (provider as IDisposable)?.Dispose(); }
            break;
        }
        default:
            Console.Error.WriteLine("Usage: extract <epub> <artifact.json> | match <artifact.json> <settings.json> <transcript.json> <match.json> | transcribe <ISTtProvider-assembly.dll> <clip.wav> <transcript.json>");
            return 2;
    }
    return 0;
}
catch (SttException exception)
{
    Console.Error.WriteLine($"Transcription failed: {exception.Code}. No automatic retry.");
    return 3;
}
catch (Exception exception) when (exception is IOException or ArgumentException or JsonException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

public sealed record ExtractedArtifact(string SourceSha256, string ExtractorVersion, BookTextExtractedDocument Document);
public sealed record TranscriptArtifact(string AudioSha256, DateTimeOffset RecordedAt, double ElapsedMilliseconds, SttResult Result);
