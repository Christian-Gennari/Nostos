using FluentAssertions;
using Nostos.Backend.Data;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 11 guard for portable bulk operations. <c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c> and raw SQL bypass the change tracker, so they cannot
/// rely on the <see cref="NostosDbContext.SaveChanges"/> revision guard. Every
/// call on a portable set must advance the revision explicitly at the call site
/// (or the same method must contain a tracked portable save, which the guard
/// covers). This source scan fails when a new call appears without one of
/// those, forcing the author to make the revision decision deliberately.
/// </summary>
public sealed class PortableBulkOperationScanTests
{
    /// <summary>
    /// Portable DbSet property names, derived from the shared product set. A
    /// call site is portable when the statement mentions one of these sets (or
    /// an owned portable collection) and is not one of the migration-operational
    /// sets below.
    /// </summary>
    private static readonly string[] PortableSetNames =
        PortableEntitySet.EntityTypes
            .Select(type => type.Name)
            .Concat(
            [
                // DbSet properties whose entity names differ from the CLR type.
                "Books", "Works", "Writings", "WritingNotes", "Notes", "Collections",
                "BookCollections", "Topics", "NoteTopics", "BookAcquisitions",
                "AssistantSettings", "NoteImportBookLinks",
            ])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Files that operate on host-operational migration records only. Their bulk
    /// calls never touch portable state; the completeness inventory classifies
    /// every entity they mutate as ExplicitlyExcluded.
    /// </summary>
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        ["Nostos.Product/Services/Portability/Migration/EfMigrationJobStore.cs"] =
            "bulk calls mutate MigrationJobRecord only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Portability/Migration/ExportArtifactPhaseHandler.cs"] =
            "bulk calls mutate MigrationExportArtifactRecord only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Portability/Migration/ImportPreparationPhaseHandler.cs"] =
            "bulk calls mutate MigrationJobRecord only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Portability/Migration/MigrationJobWorker.cs"] =
            "bulk calls mutate MigrationJobRecord only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Portability/Migration/MigrationMutation.cs"] =
            "bulk calls mutate MigrationJobRecord only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Portability/Migration/MigrationTransferCleanup.cs"] =
            "bulk calls mutate migration job/session/artifact/reservation rows only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Portability/Migration/SelfHostedMigrationTransferService.cs"] =
            "bulk calls mutate migration job/session/receipt rows only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Portability/Transfers/TransferStorageCapacity.cs"] =
            "bulk calls mutate MigrationStorageReservationRecord only; ExplicitlyExcluded host-operational state",
        ["Nostos.Product/Services/Library/LibraryReceiptRetentionService.cs"] =
            "bulk calls delete operational idempotency receipts only; ExplicitlyExcluded host-operational state",
    };

    [Fact]
    public void Every_bulk_operation_on_a_portable_set_advances_the_revision_or_is_allow_listed()
    {
        var productRoot = FindProductRoot();
        var productFiles = Directory
            .EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var backendRoot = FindBackendRoot();
        var backendFiles = Directory
            .EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        var offenders = new List<string>();
        foreach (var file in productFiles.Concat(backendFiles))
        {
            var relative = RelativeToRepo(file);
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!line.Contains("ExecuteUpdate", StringComparison.Ordinal)
                    && !line.Contains("ExecuteDelete", StringComparison.Ordinal)
                    && !line.Contains("ExecuteSqlRaw", StringComparison.Ordinal)
                    && !line.Contains("ExecuteSqlInterpolated", StringComparison.Ordinal)
                    && !line.Contains("ExecuteSql", StringComparison.Ordinal))
                {
                    continue;
                }

                var statement = StatementAt(lines, index);
                if (!MentionsPortableSet(statement))
                {
                    continue;
                }

                if (AllowList.ContainsKey(relative))
                {
                    continue;
                }

                var method = EnclosingMethodName(lines, index);
                if (MethodMentionsRevisionAdvance(lines, index, method))
                {
                    continue;
                }

                offenders.Add($"{relative}:{index + 1} ({method})");
            }
        }

        offenders.Should().BeEmpty(
            "a bulk operation on a portable set must call LibraryRevision.AdvanceAsync (or rely on a tracked " +
            "portable SaveChanges in the same method); otherwise the destination revision misses the mutation. " +
            "If the call is genuinely host-operational, add it to the allow-list with a reason." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Allow_list_only_names_files_that_exist_and_contain_bulk_calls()
    {
        var repoRoot = FindRepoRoot();
        foreach (var (relative, reason) in AllowList)
        {
            reason.Should().NotBeNullOrWhiteSpace();
            var full = Path.Combine(repoRoot, relative);
            File.Exists(full).Should().BeTrue($"allow-listed file {relative} must exist");

            var text = File.ReadAllText(full);
            (text.Contains("ExecuteUpdate", StringComparison.Ordinal)
                || text.Contains("ExecuteDelete", StringComparison.Ordinal)
                || text.Contains("ExecuteSql", StringComparison.Ordinal))
                .Should().BeTrue($"{relative} is allow-listed but no longer contains a bulk call; remove the entry");
        }
    }

    private static bool MentionsPortableSet(string statement) =>
        PortableSetNames.Any(name =>
            statement.Contains($".{name}", StringComparison.Ordinal)
            || statement.Contains($"db.{name}", StringComparison.Ordinal)
            || statement.Contains($"fenced.{name}", StringComparison.Ordinal)
            || statement.Contains($"query.{name}", StringComparison.Ordinal));

    /// <summary>The bulk call plus the preceding two lines, so a set named just above is seen.</summary>
    private static string StatementAt(string[] lines, int index)
    {
        var start = Math.Max(0, index - 2);
        var end = Math.Min(lines.Length - 1, index + 4);
        return string.Join('\n', lines[start..(end + 1)]);
    }

    /// <summary>Scans the whole enclosing method for an explicit revision advance.</summary>
    private static bool MethodMentionsRevisionAdvance(string[] lines, int index, string method)
    {
        if (method.Length == 0)
        {
            return false;
        }

        // Method bodies in this codebase are small; a 120-line window is a
        // generous bound that still cannot reach the next method by accident.
        var start = Math.Max(0, index - 120);
        var end = Math.Min(lines.Length - 1, index + 120);
        for (var cursor = start; cursor <= end; cursor++)
        {
            if (lines[cursor].Contains("LibraryRevision.Advance", StringComparison.Ordinal))
            {
                return true;
            }

            // A tracked portable save in the same method is covered by the
            // SaveChanges guard; allow-list entries cover host-only saves.
            if (lines[cursor].Contains("SaveChanges", StringComparison.Ordinal)
                && MentionsPortableSet(string.Join('\n', lines[Math.Max(0, cursor - 12)..(cursor + 1)])))
            {
                return true;
            }
        }

        return false;
    }

    private static string EnclosingMethodName(string[] lines, int index)
    {
        for (var cursor = index; cursor >= 0; cursor--)
        {
            var trimmed = lines[cursor].TrimStart();
            if (trimmed.StartsWith("public ", StringComparison.Ordinal)
                || trimmed.StartsWith("private ", StringComparison.Ordinal)
                || trimmed.StartsWith("internal ", StringComparison.Ordinal)
                || trimmed.StartsWith("protected ", StringComparison.Ordinal)
                || trimmed.StartsWith("static ", StringComparison.Ordinal))
            {
                var paren = trimmed.IndexOf('(');
                if (paren > 0)
                {
                    var name = trimmed[..paren].Split(' ')[^1];
                    if (!name.Contains('=') && !name.Contains(';'))
                    {
                        return name;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static string RelativeToRepo(string path) =>
        Path.GetRelativePath(FindRepoRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepoRoot() => FindDirectory("Nostos.Product");

    private static string FindProductRoot() => Path.Combine(FindRepoRoot(), "Nostos.Product");

    private static string FindBackendRoot() => Path.Combine(FindRepoRoot(), "Nostos.Backend");

    private static string FindDirectory(string marker)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, marker);
            if (Directory.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root (looking for {marker}).");
    }
}
