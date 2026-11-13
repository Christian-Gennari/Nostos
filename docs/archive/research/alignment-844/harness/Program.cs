using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;

// Measures the product's alignment code on a real audiobook and a real text.
// Evidence tooling for issue 844; it is not part of the solution or of CI.
//
// usage: harness <audio> <text> <lang|none> <a2t|t2a|both|neg> <count> <out.json> [excerpts.json]
//        harness match <text> "<transcript>"...
//
// The product classes are used as they are: PassageMatcher, AudioAlignmentSearch
// and AlignmentService, with the real EPUB and PDF extractors. Three things are
// local: clips are cut with ffmpeg from one file, transcription is a local
// Whisper (worker.py), and anchors are kept in memory and empty for every run,
// so every search is a cold one.
if (args[0] == "match")
{
    IBookTextExtractor ex = args[1].EndsWith(".pdf") ? new PdfBookTextExtractor() : new EpubBookTextExtractor();
    await using var f = File.OpenRead(args[1]);
    var d = await ex.ExtractAsync(f);
    var m = new PassageMatcher(d);
    foreach (var q in args.Skip(2))
    {
        var r = m.Locate(q);
        Console.WriteLine($"{r.Accepted} {r.Reason} score={r.Score:F3} runner={r.RunnerUpScore:F3} | {r.Passage}");
    }
    return;
}
var audioPath = args[0]; var textPath = args[1]; var lang = args[2]; var mode = args[3];
var count = int.Parse(args[4]); var outPath = args[5];
var excerptFile = args.Length > 6 ? args[6] : null;
var inv = CultureInfo.InvariantCulture;

IBookTextExtractor extractor = textPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
    ? new PdfBookTextExtractor() : new EpubBookTextExtractor();
BookTextExtractedDocument doc;
IReadOnlyDictionary<int, IReadOnlyList<BookTextRange>>? aids = null;
await using (var fs = File.OpenRead(textPath))
{
    if (extractor is IBookTextReadingAidSource src) { var a = await src.ExtractAnnotatedAsync(fs); doc = a.Document; aids = a.ReadingAids.Count > 0 ? a.ReadingAids : null; }
    else doc = await extractor.ExtractAsync(fs);
}
// What a reader sees and sends: the page text without ruby glosses.
string Visible(BookTextArtifactBlock b)
{
    if (aids is null || !aids.TryGetValue(b.Order, out var r)) return b.Text;
    var sb = new StringBuilder(b.Text);
    foreach (var x in r.OrderByDescending(x => x.Start)) sb.Remove(x.Start, x.Length);
    return sb.ToString().Replace("（）", "");
}
var full = string.Join("\n", doc.Blocks.Select(Visible));
Console.Error.WriteLine($"reading aids in {aids?.Count ?? 0} blocks");
var duration = double.Parse(Run("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{audioPath}\"").Trim(), inv);
Console.Error.WriteLine($"doc chars={full.Length} blocks={doc.Blocks.Count} duration={duration:F0}s");

using var worker = new Worker();
var audio = new AudioBookModel { Title = "a", WorkId = Guid.NewGuid(), Duration = Fmt(duration) };
audio.FileDetails.HasFile = true; audio.FileDetails.FileName = "book.mp3"; audio.Metadata.Language = lang == "none" ? null : lang;
var text = new EBookModel { Title = "t", WorkId = audio.WorkId };
text.FileDetails.HasFile = true; text.FileDetails.FileName = Path.GetFileName(textPath);

var options = new AlignmentOptions();
options.NormalizeProbeSettings();
var anchors = new MemoryAnchors();
var transcriber = new WorkerTranscriber(worker);
AlignmentService Service(IAlignmentAnchorStore store) => new(
    new LocalCutter(audioPath), transcriber, null!, [extractor],
    new FixedDocuments(doc, aids), options, NoOpAiUsageAccountingService.Instance, store,
    new FixedRevision(), NullLogger<AlignmentService>.Instance);

var results = new List<object>();
var words = new Regex(@"[\p{L}\p{N}]+");

if (mode is "a2t" or "both")
{
    var located = 0; var last = -1; var monotonic = true;
    for (var i = 0; i < count; i++)
    {
        // Spread positions over the book, away from the very start and end.
        var pos = duration * (0.06 + 0.88 * i / Math.Max(1, count - 1));
        var sw = Stopwatch.StartNew();
        var r = await Service(new MemoryAnchors()).LocateAsync(audio, text, pos);
        var heard = transcriber.Last;
        if (r.Located) { located++; }
        var flatFull = Regex.Replace(full, @"\s+", " ");
        var where = r.SourceText is { Length: > 12 } st ? flatFull.IndexOf(st[^12..], StringComparison.Ordinal) : -1;
        Console.Error.WriteLine($"   audio {pos / duration:P1} -> text {(where < 0 ? double.NaN : where / (double)flatFull.Length):P1}");
        Console.Error.WriteLine($"a2t {pos,7:F0}s located={r.Located} reason={r.Reason} score={r.Score:F2} | heard…{Tail(heard, 70)} | book…{Tail(r.SourceText ?? "", 70)}");
        results.Add(new { dir = "a2t", pos, r.Located, r.Reason, r.Score, heard, r.SourceText, ms = sw.ElapsedMilliseconds });
    }
    Console.WriteLine($"A2T located {located}/{count}");
}

if (mode == "neg")
{
    // Audio of a DIFFERENT book (or an intro): every answer must be a decline.
    var wrong = 0;
    for (var i = 0; i < count; i++)
    {
        var pos = i == 0 ? 20 : duration * (0.03 + 0.94 * i / Math.Max(1, count - 1));
        var r = await Service(new MemoryAnchors()).LocateAsync(audio, text, pos);
        if (r.Located) { wrong++; Console.Error.WriteLine($"FALSE ACCEPT at {pos:F0}s score={r.Score:F2} heard…{Tail(transcriber.Last, 90)} | book…{Tail(r.SourceText ?? "", 90)}"); }
        results.Add(new { dir = "neg", pos, r.Located, r.Reason, r.Score, heard = transcriber.Last });
    }
    Console.WriteLine($"NEG false accepts {wrong}/{count}");
}

if (mode is "t2a" or "both")
{
    List<string> excerpts;
    if (excerptFile is not null)
        excerpts = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(excerptFile))!;
    else
    {
        excerpts = [];
        for (var i = 0; i < count; i++)
        {
            var at = (int)(full.Length * (0.06 + 0.88 * i / Math.Max(1, count - 1)));
            excerpts.Add(PageStartExcerpt(full, at));
        }
    }
    int ok = 0, declined = 0; var deltas = new List<double>();
    foreach (var cold in new[] { true })
    foreach (var excerpt in excerpts)
    {
        var sw = Stopwatch.StartNew();
        var r = await Service(new MemoryAnchors()).LocateAudioAsync(text, audio, excerpt);
        double? delta = null; string heardAt = "";
        if (r.Located)
        {
            // Independent check: where, in the audio after the landing, do the excerpt's first words start?
            var from = Math.Max(0, r.PositionSeconds!.Value - 12);
            var reference = worker.Transcribe(LocalCutter.Cut(audioPath, from, 45), lang == "none" ? null : lang);
            var t0 = FindStart(reference, excerpt);
            if (t0 is not null) { delta = from + t0.Value - r.PositionSeconds.Value; deltas.Add(delta.Value); ok++; }
            heardAt = reference.GetProperty("text").GetString() ?? "";
        }
        else declined++;
        Console.Error.WriteLine($"t2a located={r.Located} reason={r.Reason} probes={r.Probes} pos={r.PositionSeconds:F1} approx={r.ApproximatePositionSeconds:F1} sentenceStartsAfter={delta:F1}s | {excerpt[..Math.Min(60, excerpt.Length)]}");
        results.Add(new { dir = "t2a", excerpt, r.Located, r.Reason, r.Probes, r.PositionSeconds, r.ApproximatePositionSeconds, sentenceStartsAfterSeconds = delta, ms = sw.ElapsedMilliseconds });
    }
    deltas.Sort();
    Console.WriteLine($"T2A located+verified {ok}/{excerpts.Count}, declined {declined}, unverified {excerpts.Count - ok - declined}; " +
        (deltas.Count > 0 ? $"sentence starts after landing by min {deltas[0]:F1}s median {deltas[deltas.Count / 2]:F1}s max {deltas[^1]:F1}s; late(<0) {deltas.Count(d => d < -0.5)}" : ""));
}

File.WriteAllText(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

string Tail(string s, int n) { s = Regex.Replace(s, @"\s+", " ").Trim(); return s.Length <= n ? s : s[^n..]; }

// The first text on a "page": like the EPUB reader, prefer the next sentence start.
string PageStartExcerpt(string all, int at)
{
    var m = Regex.Match(all[at..Math.Min(all.Length, at + 600)], @"[.!?。！？][""'”’)」』]?\s*(?=\S)");
    var start = m.Success ? at + m.Index + m.Length : at;
    var flat = Regex.Replace(all[start..Math.Min(all.Length, start + 700)], @"\s+", " ").Trim();
    if (flat.Length <= 280) return flat;
    var cut = flat[..280]; var sp = cut.LastIndexOf(' ');
    return (sp >= 200 ? cut[..sp] : cut).TrimEnd();
}

// Time at which the excerpt's opening words begin, from word timestamps.
double? FindStart(JsonElement reference, string excerpt)
{
    var heard = new List<(string w, double start)>();
    foreach (var w in reference.GetProperty("words").EnumerateArray())
        foreach (var t in AlignmentTokens.Split(w.GetProperty("word").GetString() ?? ""))
            heard.Add((t.Value, w.GetProperty("start").GetDouble()));
    var all = AlignmentTokens.Split(excerpt).Select(m => m.Value).ToArray();
    var size = Tokens.IsUnspaced(excerpt) ? 14 : 8;
    double best = 0; double? at = null;
    // A page excerpt can open with a running head or misread words: try later windows too.
    for (var k = 0; k <= 8 && k + size <= all.Length; k++)
    {
        var want = all[k..(k + size)];
        for (var i = 0; i + want.Length <= heard.Count; i++)
        {
            var hit = 0;
            for (var j = 0; j < want.Length; j++) if (heard[i + j].w == want[j]) hit++;
            var score = hit / (double)want.Length - k * 0.001;
            if (score > best) { best = score; at = heard[i].start; }
        }
        if (best >= 0.85) break;
    }
    return best >= 0.6 ? at : null;
}

static string Fmt(double seconds) { var t = (int)Math.Floor(seconds); return $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}"; }
static string Run(string exe, string a)
{
    var p = Process.Start(new ProcessStartInfo(exe, a) { RedirectStandardOutput = true })!;
    var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o;
}

static class Tokens
{
    static readonly Regex Unspaced = new(@"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsThai}]");
    static readonly Regex Each = new(@"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsThai}]|[\p{L}\p{N}]+");
    public static bool IsUnspaced(string s) => Unspaced.Matches(s).Count > s.Length / 4;
    public static MatchCollection Split(string s) => Each.Matches(s);
}

sealed class Worker : IDisposable
{
    readonly Process p;
    public Worker()
    {
        // ALIGNMENT_PYTHON: the interpreter that has faster-whisper installed.
        // ALIGNMENT_LD_LIBRARY_PATH: its CUDA libraries, when it needs them.
        var psi = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ALIGNMENT_PYTHON") ?? "python3",
            Path.Combine(AppContext.BaseDirectory, "worker.py"))
        { RedirectStandardInput = true, RedirectStandardOutput = true };
        if (Environment.GetEnvironmentVariable("ALIGNMENT_LD_LIBRARY_PATH") is { Length: > 0 } libraries)
            psi.Environment["LD_LIBRARY_PATH"] = libraries;
        p = Process.Start(psi)!;
        p.StandardOutput.ReadLine();
    }
    public JsonElement Transcribe(byte[] wav, string? language)
    {
        var path = Path.Combine(Path.GetTempPath(), $"n844-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);
        try
        {
            lock (p)
            {
                p.StandardInput.WriteLine(JsonSerializer.Serialize(new { path, language }));
                p.StandardInput.Flush();
                var line = p.StandardOutput.ReadLine() ?? throw new IOException("worker died");
                var json = JsonDocument.Parse(line).RootElement;
                if (json.TryGetProperty("error", out var e)) throw new IOException(e.GetString());
                return json;
            }
        }
        finally { File.Delete(path); }
    }
    public void Dispose() { try { p.Kill(); } catch { } }
}

sealed class WorkerTranscriber(Worker worker) : IAlignmentTranscriber
{
    public string Last = "";
    public int Calls;
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<SttResult> TranscribeAsync(Stream audio, string fileName, string? contentType, string? languageHint, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(); audio.CopyTo(ms);
        var r = worker.Transcribe(ms.ToArray(), languageHint);
        Calls++;
        Last = r.GetProperty("text").GetString() ?? "";
        return Task.FromResult(new SttResult(Last, r.GetProperty("language").GetString(), r.GetProperty("duration").GetDouble()));
    }
}

sealed class LocalCutter(string path) : IAudioClipCutter
{
    public static byte[] Cut(string file, double start, double seconds)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-ss", start.ToString("F3", CultureInfo.InvariantCulture), "-t", seconds.ToString("F3", CultureInfo.InvariantCulture), "-i", file, "-ac", "1", "-ar", "16000", "-f", "wav", "pipe:1" })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        return ms.ToArray();
    }
    // Same contract as the product cutter: the clip ENDS at positionSeconds.
    public Task<AudioClip> CutAsync(AudioBookModel book, double positionSeconds, double clipSeconds, CancellationToken ct = default)
    {
        var start = Math.Max(0, positionSeconds - clipSeconds);
        var bytes = Cut(path, start, positionSeconds - start);
        return Task.FromResult(new AudioClip(new MemoryStream(bytes), positionSeconds - start));
    }
}

sealed class FixedDocuments(BookTextExtractedDocument doc, IReadOnlyDictionary<int, IReadOnlyList<BookTextRange>>? aids) : IAlignmentDocumentProvider
{
    public Task<AlignmentDocumentSnapshot> GetAsync(Guid bookId, IBookAssetStorage storage, IBookTextExtractor extractor, CancellationToken ct = default) =>
        Task.FromResult(new AlignmentDocumentSnapshot(doc, "text-rev", aids));
}

sealed class FixedRevision : IAlignmentAudioRevisionProvider
{
    public Task<string?> GetAsync(AudioBookModel book, CancellationToken ct = default) => Task.FromResult<string?>("audio-rev");
}

sealed class MemoryAnchors : IAlignmentAnchorStore
{
    public readonly List<AlignmentAnchorModel> Items = [];
    public Task<bool> SaveAsync(AlignmentAnchorInput a, CancellationToken ct = default)
    {
        Items.Add(new AlignmentAnchorModel { AudioPositionMs = a.AudioPositionMs, TextGlobalOffset = a.TextGlobalOffset });
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<AlignmentAnchorModel>> GetNearbyAsync(Guid x, Guid y, string ar, string tr, long ms, long radius, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AlignmentAnchorModel>>([]);
    public Task<AlignmentAnchorModel?> AtOrBeforeTextAsync(Guid x, Guid y, string ar, string tr, int o, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(i => i.TextGlobalOffset <= o).OrderByDescending(i => i.TextGlobalOffset).FirstOrDefault());
    public Task<AlignmentAnchorModel?> AtOrAfterTextAsync(Guid x, Guid y, string ar, string tr, int o, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(i => i.TextGlobalOffset >= o).OrderBy(i => i.TextGlobalOffset).FirstOrDefault());
    public Task<AlignmentAnchorModel?> AtOrBeforeAudioAsync(Guid x, Guid y, string ar, string tr, long ms, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(i => i.AudioPositionMs <= ms).OrderByDescending(i => i.AudioPositionMs).FirstOrDefault());
    public Task<AlignmentAnchorModel?> AtOrAfterAudioAsync(Guid x, Guid y, string ar, string tr, long ms, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(i => i.AudioPositionMs >= ms).OrderBy(i => i.AudioPositionMs).FirstOrDefault());
}
