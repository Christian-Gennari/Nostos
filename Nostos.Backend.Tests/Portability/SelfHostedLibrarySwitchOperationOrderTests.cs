using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Characterization (golden) coverage for the ordered filesystem effects of the
/// two journaled library switches (#1604).
/// </summary>
/// <remarks>
/// <para>
/// Activation and recovery restore execute the same crash-sensitive protocol:
/// every durable journal phase is written before the rename it authorises, the
/// live database is quiesced before the previous generation is retained, and the
/// candidate components are renamed into place one at a time. #1604 deduplicates
/// that protocol into one implementation, so the observable order of the
/// filesystem operations is pinned first and must stay identical afterwards.
/// </para>
/// <para>
/// At every named boundary the test diffs the database volume and the media
/// volume against the previous boundary and records which paths appeared,
/// disappeared or changed size. Paths are rendered relative to their volume with
/// the switch identifier as <c>&lt;job&gt;</c> and any book identifier as
/// <c>&lt;id&gt;</c>, and media contents are summarised by the immediate file
/// count and byte total of their directory, so the golden file is stable across
/// runs. The crash outcome at each of those boundaries is pinned separately by
/// <see cref="ActivationCoordinatorCrashMatrixTests"/> and
/// <see cref="RecoveryRestoreCrashMatrixTests"/>; this test additionally proves
/// the two boundary sets are exactly the boundaries the protocol still emits.
/// </para>
/// <para>
/// To intentionally accept a protocol change, run the test once with
/// <c>NOSTOS_UPDATE_PORTABILITY_GOLDEN=1</c> and review the diff; CI never sets it.
/// </para>
/// </remarks>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class SelfHostedLibrarySwitchOperationOrderTests
{
    private const string UpdateGoldenVariable = "NOSTOS_UPDATE_PORTABILITY_GOLDEN";

    [Fact]
    public async Task Populated_activation_writes_and_renames_in_the_pinned_order()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var log = new FileSystemOperationLog(bed.Paths, bed.JobId);

        var result = await bed.ActivateAsync(confirm: true, observer: log.Record);

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        AssertGolden("activation-populated.txt", log.Render());
        AssertEveryCrashBoundaryIsObserved(
            log, ActivationCoordinatorCrashMatrixTests.Cases().Select(testCase => (string)testCase[0]));
    }

    [Fact]
    public async Task Empty_activation_writes_and_renames_in_the_pinned_order()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);
        var log = new FileSystemOperationLog(bed.Paths, bed.JobId);

        var result = await bed.ActivateAsync(confirm: true, observer: log.Record);

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        AssertGolden("activation-empty.txt", log.Render());
    }

    [Fact]
    public async Task Recovery_restore_writes_and_renames_in_the_pinned_order()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();

        var log = new FileSystemOperationLog(bed.Paths, bed.RecoveryId);
        var result = await bed.RestoreAsync(confirm: true, observer: log.Record);

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        AssertGolden("restore.txt", log.Render());
        AssertEveryCrashBoundaryIsObserved(log, RecoveryRestoreCrashMatrixTests.Boundaries);
    }

    /// <summary>
    /// A boundary the crash matrix crashes at must still be emitted by the
    /// protocol: the matrix proves the recovery outcome, the golden file proves
    /// the order, and neither is complete if a step silently stops firing.
    /// </summary>
    private static void AssertEveryCrashBoundaryIsObserved(
        FileSystemOperationLog log,
        IEnumerable<string> boundaries)
    {
        var observed = log.Steps.ToHashSet(StringComparer.Ordinal);
        boundaries.Should().OnlyContain(
            boundary => observed.Contains(boundary),
            "every crash-matrix boundary must still be emitted by the cutover protocol");
    }

    private static void AssertGolden(string relativePath, string actual)
    {
        var path = Path.Combine(GoldenRoot, relativePath);
        if (Environment.GetEnvironmentVariable(UpdateGoldenVariable) == "1")
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the golden file {path} must be committed; regenerate it once with " +
            $"{UpdateGoldenVariable}=1 and review the diff");

        var expected = File.ReadAllText(path, Encoding.UTF8);
        actual.Should().Be(
            expected,
            $"the cutover protocol order pinned in {relativePath} is crash-sensitive and must not drift");
    }

    private static string GoldenRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, "Nostos.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new InvalidOperationException("Could not locate the repository root.");
            }

            return Path.Combine(
                directory.FullName, "Nostos.Backend.Tests", "Portability", "Golden", "LibrarySwitchOrder");
        }
    }

    /// <summary>
    /// Records, at every named boundary, which paths on the database and media
    /// volumes appeared or disappeared since the previous boundary. Both volumes
    /// are compared by their path relative to the volume root, so the record never
    /// carries a host path, and every media directory whose name is a book
    /// identifier is labelled with a digest of its contents, so the record is
    /// stable across runs and shows the media tree moving intact.
    /// </summary>
    private sealed class FileSystemOperationLog
    {
        private readonly string _databaseRoot;
        private readonly string _mediaRoot;
        private readonly string _switchId;
        private readonly List<string> _steps = [];
        private readonly List<string> _lines = [];
        private SortedSet<string> _previous;

        internal FileSystemOperationLog(SelfHostedActivationPaths paths, Guid switchId)
        {
            _databaseRoot = Path.GetDirectoryName(paths.LiveDatabase)!;
            _mediaRoot = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(paths.LiveMedia)!);
            _switchId = switchId.ToString("N");
            _previous = Snapshot();
        }

        internal IReadOnlyList<string> Steps => _steps;

        internal void Record(string step)
        {
            var current = Snapshot();
            var operations = new List<string>();
            foreach (var path in current.Where(path => !_previous.Contains(path)))
            {
                operations.Add("+" + path);
            }

            foreach (var path in _previous.Where(path => !current.Contains(path)))
            {
                operations.Add("-" + path);
            }

            operations.Sort(StringComparer.Ordinal);
            _steps.Add(step);
            _lines.Add(step);
            foreach (var operation in operations)
            {
                _lines.Add("  " + operation);
            }

            _previous = current;
        }

        internal string Render() => string.Join("\n", _lines) + "\n";

        private SortedSet<string> Snapshot()
        {
            var snapshot = new SortedSet<string>(StringComparer.Ordinal);
            AddDirectories(snapshot, _databaseRoot, "db:");
            AddDirectories(snapshot, _mediaRoot, "media:");
            AddDatabaseVolumeFiles(snapshot, _databaseRoot);
            return snapshot;
        }

        private void AddDirectories(SortedSet<string> snapshot, string root, string prefix)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            snapshot.Add(Normalize(prefix, root, root, directory: true));
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                snapshot.Add(Normalize(prefix, root, directory, directory: true));
            }
        }

        private void AddDatabaseVolumeFiles(SortedSet<string> snapshot, string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                snapshot.Add(Normalize("db:", root, file, directory: false));
            }
        }

        private string Normalize(string prefix, string volumeRoot, string path, bool directory)
        {
            var relative = Path.GetRelativePath(volumeRoot, path);
            var segments = relative == "."
                ? []
                : relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var rendered = new string[segments.Length];
            var cursor = volumeRoot;
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var full = Path.Combine(cursor, segment);
                rendered[index] = Describe(prefix, segment, full, index < segments.Length - 1 || directory);
                cursor = full;
            }

            var label = string.Join('/', rendered);
            return directory ? $"{prefix}/{label}/" : $"{prefix}/{label}";
        }

        /// <summary>
        /// A media directory is identified by its contents, not by the fixture's
        /// random identifier: two books can never collapse into one path, and the
        /// same tree keeps its label when it is renamed from the candidate area to
        /// the live media root. A control-area identifier on the database volume is
        /// rendered as <c>&lt;id&gt;</c>, because only its existence is recorded and
        /// the journal it names changes on every phase write.
        /// </summary>
        private string Describe(string prefix, string segment, string fullPath, bool directory)
        {
            if (string.Equals(segment, _switchId, StringComparison.OrdinalIgnoreCase))
            {
                return "<job>";
            }

            if (!Guid.TryParse(segment, out var identifier) || identifier == Guid.Empty)
            {
                return segment;
            }

            return directory && prefix == "media:"
                ? "<" + DirectoryDigest(fullPath) + ">"
                : "<id>";
        }

        /// <summary>
        /// Short digest of the files directly inside a directory (name, length and
        /// content hash), so the label distinguishes the fixture's books and proves
        /// their bytes moved with the directory. Only immediate files are hashed:
        /// a control directory that later gains a subdirectory must keep its label.
        /// </summary>
        private static string DirectoryDigest(string directory)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in Directory
                         .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(file);
                hash.AppendData(Encoding.UTF8.GetBytes(
                    Path.GetRelativePath(directory, file).Replace('\\', '/') + ":" + bytes.Length + ":"));
                hash.AppendData(SHA256.HashData(bytes));
            }

            return Convert.ToHexString(hash.GetHashAndReset())[..8].ToLowerInvariant();
        }
    }
}
