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
    public void Comparison_specs_cover_every_archive_record_and_property()
    {
        var discovered = DiscoverRecordTypes();
        var specs = PortableLibraryVerifier.CandidateComparisonSpecs;
        var failures = new List<string>();

        foreach (var record in discovered
                     .Where(record => !specs.ContainsKey(record.Name))
                     .OrderBy(record => record.Name, StringComparer.Ordinal))
        {
            failures.Add(
                $"UNCOVERED ARCHIVE RECORD: {record.Name}. Register an executable comparison table.");
        }

        foreach (var stale in specs.Keys
                     .Where(name => discovered.All(record => record.Name != name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE COMPARISON RECORD: {stale} is registered but is not reachable from the portable archive payload.");
        }

        foreach (var record in discovered.OrderBy(record => record.Name, StringComparer.Ordinal))
        {
            if (!specs.TryGetValue(record.Name, out var spec))
            {
                continue;
            }

            var properties = record
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod is not null)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

            var compared = spec.ComparedProperties.ToHashSet(StringComparer.Ordinal);
            var excluded = spec.NotComparedProperties;

            foreach (var overlap in compared.Intersect(excluded.Keys).OrderBy(name => name, StringComparer.Ordinal))
            {
                failures.Add($"CONFLICTING COVERAGE: {record.Name}.{overlap} is compared and excluded.");
            }

            foreach (var uncovered in properties
                         .Where(property => !compared.Contains(property) && !excluded.ContainsKey(property))
                         .OrderBy(property => property, StringComparer.Ordinal))
            {
                failures.Add(
                    $"UNCOVERED ARCHIVE PROPERTY: {record.Name}.{uncovered}. Add it to an executable comparison table or record a derived-state reason.");
            }

            foreach (var missingReason in excluded
                         .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                         .Select(pair => pair.Key)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                failures.Add($"MISSING EXCLUSION REASON: {record.Name}.{missingReason}.");
            }

            foreach (var stale in compared
                         .Concat(excluded.Keys)
                         .Where(property => !properties.Contains(property))
                         .OrderBy(property => property, StringComparer.Ordinal))
            {
                failures.Add($"STALE COVERAGE PROPERTY: {record.Name}.{stale} does not exist.");
            }
        }

        failures.Should().BeEmpty(
            "comparison specs must cover every reachable portable archive record and property; a new " +
            "portable entity or field without a comparison entry must fail this test.{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, failures.Select(failure => $" - {failure}")));
    }

    [Fact]
    public void Comparison_specs_annotate_every_inventory_portable_property()
    {
        var portable = PortableCompletenessInventoryTests.PortablePropertiesByEntity;
        var annotated = PortableLibraryVerifier.CandidateComparisonSpecs.Values
            .SelectMany(spec => spec.CandidateFields)
            .Select(field => (field.Entity, field.Property))
            .ToHashSet();
        var failures = new List<string>();

        foreach (var (entity, properties) in portable)
        {
            foreach (var property in properties)
            {
                if (!annotated.Contains((entity, property)))
                {
                    failures.Add(
                        $"UNCOMPARED PORTABLE EF PROPERTY: {entity}.{property} is classified portable in the " +
                        "completeness inventory but no executable comparison entry checks it.");
                }
            }
        }

        var portableSet = portable
            .SelectMany(pair => pair.Value.Select(property => (Entity: pair.Key, Property: property)))
            .ToHashSet();
        foreach (var (entity, property) in annotated.Except(portableSet).OrderBy(field => field.Entity, StringComparer.Ordinal))
        {
            failures.Add(
                $"STALE COMPARISON ANNOTATION: {entity}.{property} is not a portable property in the completeness inventory.");
        }

        failures.Should().BeEmpty(
            "every portable EF property classified by the completeness inventory must be covered by an " +
            "executable comparison entry, and every annotation must correspond to one.{0}{1}",
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
