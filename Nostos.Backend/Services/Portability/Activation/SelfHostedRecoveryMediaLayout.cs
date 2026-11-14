using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// The on-disk media layout the library switch protocol reads: a book directory
/// is one canonical rendering of a book identifier, and every file inside it is
/// classified by its canonical file name. Retention (activation) and restore
/// (recovery) walk the same trees, so they must agree on both rules exactly.
/// </summary>
internal static class SelfHostedRecoveryMediaLayout
{
    /// <summary>
    /// A media directory whose name is one canonical rendering of a nonempty
    /// book identifier. The live <c>FileStorageService</c> layout and the
    /// activation candidate builder both use the default "D" rendering, so the
    /// recovery retention pass must accept exactly that form; the compact "N"
    /// form stays accepted because the protocol fixtures and any pre-existing
    /// control trees use it. Any other spelling fails closed.
    /// </summary>
    internal static bool TryReadBookDirectoryName(string name, out Guid bookId)
    {
        bookId = Guid.Empty;
        if (!Guid.TryParse(name, out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        if (!string.Equals(name, parsed.ToString("D"), StringComparison.Ordinal)
            && !string.Equals(name, parsed.ToString("N"), StringComparison.Ordinal))
        {
            return false;
        }

        bookId = parsed;
        return true;
    }

    /// <summary>
    /// Classifies one media file by its canonical name: the portable book file,
    /// the cover, a cover thumbnail, a multi-track audiobook track, or an
    /// interrupted download. Anything else stays "other" rather than being
    /// guessed into a portable kind.
    /// </summary>
    internal static (string Kind, string Extension) ClassifyMediaFile(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
        {
            return ("partial", extension);
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.Equals(stem, "book", StringComparison.OrdinalIgnoreCase))
        {
            return ("book", extension);
        }

        if (string.Equals(stem, "cover", StringComparison.OrdinalIgnoreCase))
        {
            return ("cover", extension);
        }

        if (stem.StartsWith("cover-thumb-", StringComparison.OrdinalIgnoreCase))
        {
            return ("thumbnail", extension);
        }

        // A track of a multi-track audiobook. Exact, case-correct name only:
        // anything that merely resembles one stays "other".
        if (BookTrackFormats.TryParseCanonicalFileName(fileName, out _))
        {
            return ("track", extension);
        }

        return ("other", extension);
    }
}
