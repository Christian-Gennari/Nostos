namespace Nostos.Backend.Providers.Discovery;

/// <summary>Resource bounds for aggregate provider discovery.</summary>
public sealed class ProviderDiscoveryOptions
{
    public const string SectionName = "ProviderDiscovery";

    public static readonly TimeSpan DefaultSearchTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Default interactive budget for the whole Add Book search (#641). A
    /// conservative starting point in the low single digits, not a measured
    /// provider percentile: public catalogues answer typical searches well
    /// inside it, and one unhealthy source should cost the picker seconds, not
    /// the full per-provider deadline. Tune it with
    /// <c>ProviderDiscovery:AggregateSearchTimeout</c> once real timings exist.
    /// </summary>
    public static readonly TimeSpan DefaultAggregateSearchTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Hard deadline for one provider participating in aggregate discovery.
    /// Non-positive values fall back to the finite default.
    /// </summary>
    public TimeSpan SearchTimeout { get; set; } = DefaultSearchTimeout;

    /// <summary>
    /// Hard ceiling for one aggregate search, across every provider. Providers
    /// still pending when it expires are cancelled and reported as timed out,
    /// while siblings that answered in time contribute their results.
    /// Non-positive values fall back to the finite default.
    /// </summary>
    public TimeSpan AggregateSearchTimeout { get; set; } = DefaultAggregateSearchTimeout;

    public TimeSpan EffectiveSearchTimeout =>
        SearchTimeout > TimeSpan.Zero
            ? SearchTimeout
            : DefaultSearchTimeout;

    public TimeSpan EffectiveAggregateSearchTimeout =>
        AggregateSearchTimeout > TimeSpan.Zero
            ? AggregateSearchTimeout
            : DefaultAggregateSearchTimeout;

    /// <summary>
    /// How long one provider may take inside an aggregate search. Every
    /// eligible provider starts at the same moment, so capping each wait at the
    /// aggregate budget bounds the whole response.
    /// </summary>
    public TimeSpan EffectiveProviderDeadline =>
        EffectiveSearchTimeout < EffectiveAggregateSearchTimeout
            ? EffectiveSearchTimeout
            : EffectiveAggregateSearchTimeout;
}
