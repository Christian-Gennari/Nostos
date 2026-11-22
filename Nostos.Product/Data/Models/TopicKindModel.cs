using Microsoft.EntityFrameworkCore;

namespace Nostos.Backend.Data.Models;

/// <summary>
/// A reader-editable category for topics (e.g. Person, Idea, Place). At most
/// <see cref="MaxKinds"/> kinds exist in a library, each holding a unique
/// folded name and a unique palette color key (<c>kind-1</c> .. <c>kind-12</c>).
/// </summary>
public class TopicKindModel
{
    public const int MaxKinds = 12;
    public const int MaxNameLength = 100;
    public const int BuiltInSeedCount = 6;

    public static readonly DateTime BuiltInSeedCreatedAt =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly Guid PersonId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public static readonly Guid IdeaId = Guid.Parse("11111111-0000-0000-0000-000000000002");
    public static readonly Guid PlaceId = Guid.Parse("11111111-0000-0000-0000-000000000003");
    public static readonly Guid WorkId = Guid.Parse("11111111-0000-0000-0000-000000000004");
    public static readonly Guid EventId = Guid.Parse("11111111-0000-0000-0000-000000000005");
    public static readonly Guid FieldId = Guid.Parse("11111111-0000-0000-0000-000000000006");

    public static readonly IReadOnlyList<string> PaletteColorKeys =
    [
        "kind-1",
        "kind-2",
        "kind-3",
        "kind-4",
        "kind-5",
        "kind-6",
        "kind-7",
        "kind-8",
        "kind-9",
        "kind-10",
        "kind-11",
        "kind-12",
    ];

    private static readonly HashSet<string> ValidColorKeys =
        new(PaletteColorKeys, StringComparer.Ordinal);

    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = string.Empty;

    /// <summary>The kind name as displayed. Setting it updates <see cref="NormalizedName"/>.</summary>
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            NormalizedName = NormalizeName(value);
        }
    }

    public string NormalizedName { get; set; } = string.Empty;

    public string ColorKey { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TopicModel> Topics { get; set; } = [];

    public static string NormalizeName(string? name) => TopicModel.NormalizeName(name);

    public static bool IsValidColorKey(string? colorKey) =>
        colorKey is not null && ValidColorKeys.Contains(colorKey);

    public static TopicKindModel[] CreateBuiltInSeeds() =>
    [
        new() { Id = PersonId, Name = "Person", ColorKey = "kind-1", SortOrder = 1, CreatedAt = BuiltInSeedCreatedAt },
        new() { Id = IdeaId, Name = "Idea", ColorKey = "kind-2", SortOrder = 2, CreatedAt = BuiltInSeedCreatedAt },
        new() { Id = PlaceId, Name = "Place", ColorKey = "kind-3", SortOrder = 3, CreatedAt = BuiltInSeedCreatedAt },
        new() { Id = WorkId, Name = "Work", ColorKey = "kind-4", SortOrder = 4, CreatedAt = BuiltInSeedCreatedAt },
        new() { Id = EventId, Name = "Event", ColorKey = "kind-5", SortOrder = 5, CreatedAt = BuiltInSeedCreatedAt },
        new() { Id = FieldId, Name = "Field", ColorKey = "kind-6", SortOrder = 6, CreatedAt = BuiltInSeedCreatedAt },
    ];

    public static bool MatchesBuiltInDefaults(IEnumerable<TopicKindModel> rows)
    {
        var list = rows as IReadOnlyList<TopicKindModel> ?? rows.ToList();
        if (list.Count != BuiltInSeedCount)
        {
            return false;
        }

        var byId = list.ToDictionary(row => row.Id);
        foreach (var seed in CreateBuiltInSeeds())
        {
            if (!byId.TryGetValue(seed.Id, out var actual)
                || !string.Equals(actual.Name, seed.Name, StringComparison.Ordinal)
                || !string.Equals(actual.NormalizedName, seed.NormalizedName, StringComparison.Ordinal)
                || !string.Equals(actual.ColorKey, seed.ColorKey, StringComparison.Ordinal)
                || actual.SortOrder != seed.SortOrder)
            {
                return false;
            }
        }

        return true;
    }

    public static async Task<bool> AreDefaultsUntouchedAsync(
        NostosDbContext db,
        CancellationToken ct = default)
    {
        var rows = await db.TopicKinds.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return MatchesBuiltInDefaults(rows);
    }
}

public static class TopicKindSources
{
    public const string Reader = "reader";
    public const string Assistant = "assistant";
    public const string ConnectedApp = "connected-app";

    public static readonly IReadOnlyList<string> All = [Reader, Assistant, ConnectedApp];

    public static bool IsValid(string? source) =>
        source is Reader or Assistant or ConnectedApp;
}
