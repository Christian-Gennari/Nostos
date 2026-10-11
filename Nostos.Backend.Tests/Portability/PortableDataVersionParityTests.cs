using System.Text.RegularExpressions;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The browser inspects an archive before it sends a single request, with its
/// own list of supported data versions in
/// <c>Nostos.Frontend/src/app/library-transfer/services/migration-preflight.ts</c>.
/// When the server moved to data version 4 and that list stayed at 1-3, every
/// fresh export was refused in the import dialog and no test failed. Bumping
/// <see cref="PortableArchiveFormat.DataVersion"/> now fails here until the
/// browser list follows.
/// </summary>
public sealed class PortableDataVersionParityTests
{
    private const string BrowserSource =
        "Nostos.Frontend/src/app/library-transfer/services/migration-preflight.ts";

    [Fact]
    public void BrowserAcceptsEveryDataVersionTheServerWritesOrReads()
    {
        var browser = ReadBrowserSupportedDataVersions();

        browser.Should().Contain(
            PortableArchiveFormat.DataVersion,
            "the browser must accept the version this build exports");
        browser.Should().BeEquivalentTo(
            Enumerable.Range(1, PortableArchiveFormat.DataVersion),
            "the server preflight accepts every version up to the current one");
    }

    private static int[] ReadBrowserSupportedDataVersions()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), BrowserSource));
        var match = Regex.Match(
            source,
            @"SUPPORTED_PORTABLE_DATA_VERSIONS[^=]*=\s*new Set\(\[([\d,\s]+)\]\)");
        match.Success.Should().BeTrue(
            $"{BrowserSource} must declare SUPPORTED_PORTABLE_DATA_VERSIONS as a literal set");
        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Nostos.Product")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
