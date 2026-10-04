using System.Reflection;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class PortableLibraryVerifierCoverageTests
{
    private static readonly IReadOnlySet<string> StructuralOnly =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(PortableLibraryData),
            nameof(PortableArchiveManifest),
            nameof(PortableArchiveCounts),
            nameof(PortableArchivePayload),
        };

    [Fact]
    public void Coverage_map_matches_every_archive_record_and_property()
    {
        var discovered = DiscoverRecordTypes();
        var coverage = PortableLibraryVerifier.VerificationCoverage;
        var failures = new List<string>();

        foreach (var record in discovered
                     .Where(record => !coverage.ContainsKey(record.Name))
                     .OrderBy(record => record.Name, StringComparer.Ordinal))
        {
            failures.Add(
                $"UNCOVERED ARCHIVE RECORD: {record.Name}. Register its properties in PortableLibraryVerificationCoverage.");
        }

        foreach (var stale in coverage.Keys
                     .Where(name => discovered.All(record => record.Name != name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE COVERAGE RECORD: {stale} is registered but is not reachable from PortableLibraryData.");
        }

        foreach (var record in discovered.OrderBy(record => record.Name, StringComparer.Ordinal))
        {
            if (!coverage.TryGetValue(record.Name, out var entry))
            {
                continue;
            }

            var properties = record
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod is not null)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var property in properties
                         .Where(property => !entry.ComparedProperties.Contains(property))
                         .OrderBy(property => property, StringComparer.Ordinal))
            {
                if (entry.NotComparedProperties.TryGetValue(property, out var reason)
                    && !string.IsNullOrWhiteSpace(reason))
                {
                    continue;
                }

                failures.Add(
                    $"UNCOVERED ARCHIVE PROPERTY: {record.Name}.{property}. Compare it or record a derived-state reason.");
            }

            foreach (var stale in entry.ComparedProperties
                         .Where(property => !properties.Contains(property))
                         .OrderBy(property => property, StringComparer.Ordinal))
            {
                failures.Add($"STALE COMPARED PROPERTY: {record.Name}.{stale} does not exist.");
            }

            foreach (var stale in entry.NotComparedProperties.Keys
                         .Where(property => !properties.Contains(property))
                         .OrderBy(property => property, StringComparer.Ordinal))
            {
                failures.Add($"STALE DERIVED PROPERTY: {record.Name}.{stale} does not exist.");
            }
        }

        failures.Should().BeEmpty(
            "verifier coverage must be tied to the portable completeness inventory: a newly added " +
            "portable entity or field without verification coverage must fail this test.{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, failures.Select(failure => $" - {failure}")));
    }

    [Fact]
    public void Verified_kinds_cover_every_portable_entity_record()
    {
        var expected = DiscoverRecordTypes()
            .Where(record => !StructuralOnly.Contains(record.Name))
            .Select(record => record.Name)
            .ToHashSet(StringComparer.Ordinal);

        PortableLibraryVerifier.CandidateVerifiedKinds
            .Should().BeEquivalentTo(
                expected,
                "every portable entity record must be listed as verified before a candidate verification can pass");
    }

    private static IReadOnlySet<Type> DiscoverRecordTypes()
    {
        var discovered = new HashSet<Type>();
        var pending = new Queue<Type>([typeof(PortableLibraryData), typeof(PortableArchiveManifest)]);

        while (pending.TryDequeue(out var candidate))
        {
            candidate = Nullable.GetUnderlyingType(candidate) ?? candidate;

            if (candidate == typeof(string))
            {
                continue;
            }

            if (candidate.IsArray)
            {
                pending.Enqueue(candidate.GetElementType()!);
                continue;
            }

            var enumerable = candidate
                .GetInterfaces()
                .Append(candidate)
                .FirstOrDefault(type =>
                    type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            if (enumerable is not null)
            {
                pending.Enqueue(enumerable.GetGenericArguments()[0]);
                continue;
            }

            if (!IsArchiveRecord(candidate))
            {
                if (candidate.IsGenericType)
                {
                    foreach (var argument in candidate.GetGenericArguments())
                    {
                        pending.Enqueue(argument);
                    }
                }

                continue;
            }

            if (!discovered.Add(candidate))
            {
                continue;
            }

            foreach (var property in candidate.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                pending.Enqueue(property.PropertyType);
            }
        }

        return discovered;
    }

    private static bool IsArchiveRecord(Type candidate) =>
        candidate.IsClass
        && candidate != typeof(string)
        && candidate.Assembly == typeof(PortableLibraryData).Assembly
        && candidate.Namespace == typeof(PortableLibraryData).Namespace;
}
