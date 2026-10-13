namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// One book the scan found: a single file, or a folder of ordered audio
/// tracks. A candidate is a proposal only; nothing in the library changes
/// until it is imported.
/// </summary>
public sealed record ExternalMediaCandidate(
    string Key,
    string Kind,
    string Layout,
    string Location,
    IReadOnlyList<ExternalMediaFile> Files,
    IReadOnlyList<string> Concerns,
    bool Blocked)
{
    public const string Audiobook = "audiobook";
    public const string Ebook = "ebook";

    public const string SingleFile = "file";
    public const string Tracks = "tracks";

    public long TotalBytes => Files.Sum(file => file.Length);
}

/// <summary>What a walk of the root contains, grouped into books.</summary>
public sealed record ExternalMediaGrouping(
    IReadOnlyList<ExternalMediaCandidate> Candidates,
    IReadOnlyList<string> UnsupportedMedia);

/// <summary>
/// Turns a flat file listing into candidate books. Pure: it reads no file,
/// so the same listing always groups the same way.
///
/// Rules, in order:
/// - every EPUB and PDF is an ebook of its own;
/// - every M4B is an audiobook of its own;
/// - a folder holding exactly one loose MP3/M4A is a single-file audiobook;
/// - a folder holding several loose MP3/M4A files is one multi-track
///   audiobook, its tracks in natural file name order.
///
/// A layout that could be read two ways is still proposed, but with a
/// concern attached, and is left out of "import everything that is ready".
/// </summary>
public static class ExternalMediaGrouper
{
    private static readonly HashSet<string> EbookExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".epub", ".pdf" };

    private static readonly HashSet<string> LooseAudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a" };

    /// <summary>Media a user would expect to see, which linking does not support yet.</summary>
    private static readonly HashSet<string> UnsupportedMediaExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".ogg", ".opus", ".wav", ".aac", ".wma", ".aax", ".mobi", ".azw3", ".azw", ".cbz", ".cbr",
        };

    public static string FileKey(string relativePath) => "file:" + relativePath;

    public static string TracksKey(string folder) => "tracks:" + folder;

    public static ExternalMediaGrouping Group(IReadOnlyList<ExternalMediaFile> files)
    {
        var candidates = new List<ExternalMediaCandidate>();
        var unsupported = new List<string>();
        var looseAudio = new Dictionary<string, List<ExternalMediaFile>>(StringComparer.Ordinal);
        var foldersWithM4b = new HashSet<string>(StringComparer.Ordinal);
        var foldersWithAudio = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.RelativePath);
            var folder = FolderOf(file.RelativePath);

            if (EbookExtensions.Contains(extension))
            {
                candidates.Add(Single(file, ExternalMediaCandidate.Ebook, []));
            }
            else if (string.Equals(extension, ".m4b", StringComparison.OrdinalIgnoreCase))
            {
                foldersWithM4b.Add(folder);
                foldersWithAudio.Add(folder);
                candidates.Add(Single(file, ExternalMediaCandidate.Audiobook, []));
            }
            else if (LooseAudioExtensions.Contains(extension))
            {
                foldersWithAudio.Add(folder);
                if (!looseAudio.TryGetValue(folder, out var list))
                    looseAudio[folder] = list = [];
                list.Add(file);
            }
            else if (UnsupportedMediaExtensions.Contains(extension))
            {
                unsupported.Add(file.RelativePath);
            }
        }

        foreach (var (folder, audio) in looseAudio)
        {
            var concerns = new List<string>();
            if (foldersWithM4b.Contains(folder))
                concerns.Add("This folder also holds an M4B file, so these files may belong to that book.");
            if (foldersWithAudio.Any(other => other.StartsWith(folder.Length == 0 ? "" : folder + "/", StringComparison.Ordinal)
                    && other != folder))
                concerns.Add("This folder holds audio files and also subfolders with audio.");
            if (LooksLikeDisc(folder))
                concerns.Add("This folder looks like one disc of a longer book. Each disc folder is proposed as its own book.");

            if (audio.Count == 1)
            {
                candidates.Add(Single(audio[0], ExternalMediaCandidate.Audiobook, concerns));
                continue;
            }

            if (folder.Length == 0)
                concerns.Add("These files sit directly in the linked folder, so they may be separate books rather than tracks of one.");

            audio.Sort((a, b) => NaturalOrder.Compare(FileNameOf(a.RelativePath), FileNameOf(b.RelativePath)));
            var tooMany = audio.Count > BookTrackList.MaxTracks;
            if (tooMany)
                concerns.Add($"A book can hold at most {BookTrackList.MaxTracks} tracks; this folder has {audio.Count}.");

            candidates.Add(new ExternalMediaCandidate(
                TracksKey(folder),
                ExternalMediaCandidate.Audiobook,
                ExternalMediaCandidate.Tracks,
                folder,
                audio,
                concerns,
                Blocked: tooMany));
        }

        candidates.Sort((a, b) => NaturalOrder.Compare(a.Location, b.Location));
        return new ExternalMediaGrouping(candidates, unsupported);
    }

    private static ExternalMediaCandidate Single(ExternalMediaFile file, string kind, IReadOnlyList<string> concerns) =>
        new(FileKey(file.RelativePath), kind, ExternalMediaCandidate.SingleFile, file.RelativePath, [file], concerns, Blocked: false);

    internal static string FolderOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    internal static string FileNameOf(string relativePath) =>
        relativePath[(relativePath.LastIndexOf('/') + 1)..];

    private static bool LooksLikeDisc(string folder)
    {
        var name = FileNameOf(folder).Trim().ToLowerInvariant();
        foreach (var prefix in (string[])["cd", "disc", "disk"])
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest = name[prefix.Length..].TrimStart(' ', '-', '_', '.');
            if (rest.Length > 0 && rest.All(char.IsAsciiDigit))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Orders names the way a person reads them: runs of digits compare as
/// numbers, so "2 - Intro" comes before "10 - End". Ties fall back to
/// ordinal comparison, which keeps the order total and repeatable.
/// </summary>
public static class NaturalOrder
{
    public static int Compare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var startA = i;
                var startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                var runA = a.AsSpan(startA, i - startA).TrimStart('0');
                var runB = b.AsSpan(startB, j - startB).TrimStart('0');
                if (runA.Length != runB.Length)
                    return runA.Length.CompareTo(runB.Length);

                var digits = runA.SequenceCompareTo(runB);
                if (digits != 0)
                    return digits;
                continue;
            }

            var left = char.ToUpperInvariant(a[i]);
            var right = char.ToUpperInvariant(b[j]);
            if (left != right)
                return left.CompareTo(right);
            i++;
            j++;
        }

        var remaining = (a.Length - i).CompareTo(b.Length - j);
        return remaining != 0 ? remaining : string.CompareOrdinal(a, b);
    }
}
