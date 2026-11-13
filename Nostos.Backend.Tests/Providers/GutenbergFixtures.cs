namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The recorded Project Gutenberg responses used by both the provider's
/// behaviour tests and the characterization suite, so a refactor is measured
/// against the same bytes the source really sent.
/// </summary>
internal static class GutenbergFixtures
{
    public static string Load(string filename)
    {
        var basePath = AppContext.BaseDirectory;
        var directPath = Path.Combine(basePath, "Fixtures", "gutenberg", filename);
        if (File.Exists(directPath))
            return File.ReadAllText(directPath);

        // Fallback: look upward from base directory
        var current = new DirectoryInfo(basePath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Fixtures", "gutenberg", filename);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            var nestedCandidate = Path.Combine(current.FullName, "Nostos.Backend.Tests", "Fixtures", "gutenberg", filename);
            if (File.Exists(nestedCandidate))
                return File.ReadAllText(nestedCandidate);

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Gutenberg test fixture '{filename}' could not be located in output directory or repo tree.");
    }
}
