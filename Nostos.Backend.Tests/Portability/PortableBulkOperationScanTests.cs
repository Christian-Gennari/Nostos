using FluentAssertions;
using Nostos.Backend.Data;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 11 guard for portable bulk operations. <c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c> and raw SQL bypass the change tracker, so they cannot
/// rely on the <see cref="NostosDbContext.SaveChanges"/> revision guard. Every
/// call on a portable set must advance the revision explicitly <em>before</em>
/// the bulk statement, in the same transaction, so all portable writers take
/// the singleton revision lock in one order. This source scan fails when a new
/// call appears without that shape, forcing the author to make the revision
/// decision deliberately.
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

    /// <summary>
    /// Portable bulk methods whose transaction is owned by the caller. The
    /// advance-before-bulk order is still enforced; only the local
    /// <c>BeginTransaction</c> requirement is waived.
    /// </summary>
    private static readonly Dictionary<string, string> AmbientTransactionAllowList = new(StringComparer.Ordinal)
    {
        ["Nostos.Product/Services/Library/LibraryService.cs:DeleteCollectionCoreAsync"] =
            "runs inside LibraryMutationExecutor's ambient transaction; the advance happens before the bulk delete",
    };

    [Fact]
    public void Every_bulk_operation_on_a_portable_set_advances_the_revision_first()
    {
        var repoRoot = FindRepoRoot();
        var files = new[] { "Nostos.Product", "Nostos.Backend" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(repoRoot, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var relative = RelativeToRepo(file);
            var lines = File.ReadAllLines(file);

            for (var index = 0; index < lines.Length; index++)
            {
                if (!IsBulkCall(lines[index]))
                {
                    continue;
                }

                if (!MentionsPortableSet(StatementWindow(lines, index)))
                {
                    continue;
                }

                if (AllowList.ContainsKey(relative))
                {
                    continue;
                }

                var body = EnclosingMethodBody(lines, index, out var methodName);
                if (body is null)
                {
                    offenders.Add($"{relative}:{index + 1}: bulk call outside a recognized method body");
                    continue;
                }

                var bodyLines = lines[body.Value.Start..(body.Value.End + 1)];
                var bulkOffset = index - body.Value.Start;
                var advanceOffset = Array.FindIndex(
                    bodyLines,
                    line => line.Contains("LibraryRevision.Advance", StringComparison.Ordinal));

                if (advanceOffset < 0 || advanceOffset > bulkOffset)
                {
                    offenders.Add(
                        $"{relative}:{index + 1} ({methodName}): the revision must be advanced before the bulk statement");
                    continue;
                }

                var ambientKey = $"{relative}:{methodName}";
                if (!AmbientTransactionAllowList.ContainsKey(ambientKey))
                {
                    var beginOffset = Array.FindIndex(
                        bodyLines,
                        line => line.Contains("BeginTransaction", StringComparison.Ordinal));
                    if (beginOffset < 0 || beginOffset > advanceOffset)
                    {
                        offenders.Add(
                            $"{relative}:{index + 1} ({methodName}): the advance must run inside a transaction opened before it");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "a bulk operation on a portable set must advance LibraryRevision before the statement, " +
            "inside the same transaction. If the call is genuinely host-operational, add it to the " +
            "allow-list with a reason." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Allow_lists_only_name_files_and_methods_that_exist_and_contain_bulk_calls()
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

        foreach (var (key, reason) in AmbientTransactionAllowList)
        {
            reason.Should().NotBeNullOrWhiteSpace();
            var separator = key.LastIndexOf(':');
            var relative = key[..separator];
            var method = key[(separator + 1)..];
            var full = Path.Combine(repoRoot, relative);
            File.Exists(full).Should().BeTrue($"allow-listed file {relative} must exist");
            File.ReadAllText(full).Should().Contain(
                method,
                $"{key} is allow-listed but the method no longer exists; remove the entry");
        }
    }

    private static bool IsBulkCall(string line) =>
        line.Contains("ExecuteUpdate", StringComparison.Ordinal)
        || line.Contains("ExecuteDelete", StringComparison.Ordinal)
        || line.Contains("ExecuteSqlRaw", StringComparison.Ordinal)
        || line.Contains("ExecuteSqlInterpolated", StringComparison.Ordinal)
        || line.Contains("ExecuteSql", StringComparison.Ordinal);

    private static bool MentionsPortableSet(string statement) =>
        PortableSetNames.Any(name =>
            statement.Contains($".{name}", StringComparison.Ordinal)
            || statement.Contains($"db.{name}", StringComparison.Ordinal)
            || statement.Contains($"fenced.{name}", StringComparison.Ordinal)
            || statement.Contains($"query.{name}", StringComparison.Ordinal));

    /// <summary>The bulk call plus the preceding two lines, so a set named just above is seen.</summary>
    private static string StatementWindow(string[] lines, int index)
    {
        var start = Math.Max(0, index - 2);
        var end = Math.Min(lines.Length - 1, index + 4);
        return string.Join('\n', lines[start..(end + 1)]);
    }

    /// <summary>
    /// Extracts the enclosing method body by locating the nearest preceding
    /// signature line and brace-matching to its close. Returns null when no
    /// recognizable signature precedes the call.
    /// </summary>
    private static (int Start, int End)? EnclosingMethodBody(string[] lines, int index, out string methodName)
    {
        for (var cursor = index; cursor >= 0; cursor--)
        {
            var trimmed = lines[cursor].TrimStart();
            if (!(trimmed.StartsWith("public ", StringComparison.Ordinal)
                || trimmed.StartsWith("private ", StringComparison.Ordinal)
                || trimmed.StartsWith("internal ", StringComparison.Ordinal)
                || trimmed.StartsWith("protected ", StringComparison.Ordinal)
                || trimmed.StartsWith("static ", StringComparison.Ordinal)))
            {
                continue;
            }

            var paren = trimmed.IndexOf('(');
            if (paren <= 0)
            {
                continue;
            }

            // The signature may carry a generic return type with its own
            // parentheses (e.g. Task<(bool, Dto)> MethodName(...)); the method
            // name is the last identifier directly followed by '(' on the line.
            var matches = System.Text.RegularExpressions.Regex.Matches(
                trimmed,
                @"([A-Za-z_][A-Za-z0-9_]*)\s*\(");
            if (matches.Count == 0)
            {
                continue;
            }

            var name = matches[matches.Count - 1].Groups[1].Value;
            if (name is "if" or "for" or "foreach" or "while" or "switch" or "catch" or "using" or "return")
            {
                continue;
            }

            var open = -1;
            for (var scan = cursor; scan <= Math.Min(index + 1, lines.Length - 1); scan++)
            {
                if (lines[scan].Contains('{'))
                {
                    open = scan;
                    break;
                }

                if (lines[scan].TrimEnd().EndsWith(';'))
                {
                    break;
                }
            }

            if (open < 0)
            {
                continue;
            }

            var depth = 0;
            for (var scan = open; scan < lines.Length; scan++)
            {
                foreach (var character in lines[scan])
                {
                    if (character == '{')
                    {
                        depth++;
                    }
                    else if (character == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            methodName = name;
                            return (open, scan);
                        }
                    }
                }
            }
        }

        methodName = string.Empty;
        return null;
    }

    private static string RelativeToRepo(string path) =>
        Path.GetRelativePath(FindRepoRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Nostos.Product");
            if (Directory.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
