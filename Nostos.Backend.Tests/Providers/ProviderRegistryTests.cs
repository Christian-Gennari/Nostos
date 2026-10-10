using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void ValidProvider_IsDiscoverableById_AndResolvesInterfaceBundle()
    {
        var provider = new FullFeaturedFakeProvider("open-library", "Open Library");
        var registry = new ProviderRegistry(new[] { provider });

        registry.All.Should().HaveCount(1);
        var registration = registry.Find("open-library");

        registration.Should().NotBeNull();
        registration!.Id.Should().Be("open-library");
        registration.Provider.Should().BeSameAs(provider);
        registration.Search.Should().BeSameAs(provider);
        registration.Catalog.Should().BeSameAs(provider);
        registration.Planner.Should().BeSameAs(provider);
        registration.DownloadPolicy.Should().BeSameAs(provider);
    }

    [Theory]
    [InlineData("non-existent")]
    [InlineData("unknown-provider")]
    public void UnknownId_ReturnsNull(string unknownId)
    {
        var provider = new MinimalFakeProvider("gutenberg", "Project Gutenberg");
        var registry = new ProviderRegistry(new[] { provider });

        registry.Find(unknownId).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void EmptyOrWhitespaceId_ReturnsNull(string? id)
    {
        var provider = new MinimalFakeProvider("gutenberg", "Project Gutenberg");
        var registry = new ProviderRegistry(new[] { provider });

        registry.Find(id!).Should().BeNull();
    }

    [Fact]
    public void DuplicateIds_ThrowsInvalidOperationException()
    {
        var first = new MinimalFakeProvider("gutenberg", "Project Gutenberg 1");
        var second = new MinimalFakeProvider("gutenberg", "Project Gutenberg 2");

        var act = () => new ProviderRegistry(new[] { first, second });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Two content providers claim the id 'gutenberg'*");
    }

    [Theory]
    [InlineData("Gutenberg")]
    [InlineData("ARCHIVE")]
    [InlineData("openLibrary")]
    public void UppercaseId_ThrowsInvalidOperationException(string badId)
    {
        var provider = new MinimalFakeProvider(badId, "Some Provider");

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Provider id '{badId}' is invalid*");
    }

    [Theory]
    [InlineData("open library")]
    [InlineData("open_library")]
    [InlineData("-leading-hyphen")]
    [InlineData("has.dot")]
    [InlineData("too-long-id-that-exceeds-thirty-two-characters-in-length")]
    public void InvalidCharactersOrFormatInId_ThrowsInvalidOperationException(string badId)
    {
        var provider = new MinimalFakeProvider(badId, "Some Provider");

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Provider id '{badId}' is invalid*");
    }

    [Fact]
    public void EmptyOrWhitespaceDisplayName_ThrowsInvalidOperationException()
    {
        var provider = new MinimalFakeProvider("gutenberg", "  ");

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must have a display name*");
    }

    [Fact]
    public void ProviderDeclaresSearch_WithoutImplementingIProviderSearch_ThrowsInvalidOperationException()
    {
        var provider = new MinimalFakeProvider("gutenberg", "Project Gutenberg", capabilities: ProviderCapabilities.Search);

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*declares ProviderCapabilities.Search = True but does not implement IProviderSearch*");
    }

    [Fact]
    public void ProviderImplementsIProviderSearch_WithoutDeclaringSearch_ThrowsInvalidOperationException()
    {
        var provider = new SearchOnlyFakeProvider("gutenberg", "Project Gutenberg", declaredCapabilities: ProviderCapabilities.None);

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*declares ProviderCapabilities.Search = False but does implement IProviderSearch*");
    }

    [Fact]
    public void DeclaringEbookAcquisition_WithoutIProviderAcquisitionPlanner_ThrowsInvalidOperationException()
    {
        var provider = new DownloadPolicyOnlyFakeProvider("archive", "Archive", ProviderCapabilities.EbookAcquisition);

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*declares ProviderCapabilities.{Ebook,Audiobook}Acquisition = True but does not implement IProviderAcquisitionPlanner*");
    }

    [Fact]
    public void DeclaringEbookAcquisition_WithoutIProviderDownloadPolicy_ThrowsInvalidOperationException()
    {
        var provider = new PlannerOnlyFakeProvider("archive", "Archive", ProviderCapabilities.EbookAcquisition);

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*declares ProviderCapabilities.{Ebook,Audiobook}Acquisition = True but does not implement IProviderDownloadPolicy*");
    }

    [Fact]
    public void DeclaringMultiTrackAudiobook_WithoutAudiobookAcquisition_ThrowsInvalidOperationException()
    {
        var provider = new MinimalFakeProvider("archive", "Archive", ProviderCapabilities.MultiTrackAudiobook);

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*declares ProviderCapabilities.MultiTrackAudiobook without ProviderCapabilities.AudiobookAcquisition*");
    }

    [Fact]
    public void PolicyWithNoAllowedHosts_ThrowsInvalidOperationException()
    {
        var provider = new AcquisitionFakeProvider(
            "archive",
            "Archive",
            allowedHosts: Array.Empty<string>());

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*allows no download hosts*");
    }

    [Fact]
    public void PolicyWithMaxBytesPerPartGreaterThanMaxTotalBytes_ThrowsInvalidOperationException()
    {
        var provider = new AcquisitionFakeProvider(
            "archive",
            "Archive",
            allowedHosts: new[] { "archive.org" },
            maxBytesPerPart: 200,
            maxTotalBytes: 100);

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*has MaxBytesPerPart above MaxTotalBytes*");
    }

    [Theory]
    [InlineData(0, 100, 5)]
    [InlineData(-1, 100, 5)]
    [InlineData(50, 0, 5)]
    [InlineData(50, -1, 5)]
    [InlineData(50, 100, 0)]
    [InlineData(50, 100, -1)]
    public void PolicyWithNonPositiveLimits_ThrowsInvalidOperationException(long perPart, long total, int parts)
    {
        var provider = new AcquisitionFakeProvider(
            "archive",
            "Archive",
            allowedHosts: new[] { "archive.org" },
            maxBytesPerPart: perPart,
            maxTotalBytes: total,
            maxParts: parts);

        var act = () => new ProviderRegistry(new[] { provider });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must declare positive download limits*");
    }

    #region Fake Provider Implementations

    private class MinimalFakeProvider : IContentProvider
    {
        public MinimalFakeProvider(string id, string displayName, ProviderCapabilities capabilities = ProviderCapabilities.None)
        {
            Id = id;
            DisplayName = displayName;
            Capabilities = capabilities;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public ProviderCapabilities Capabilities { get; }
        public string? RightsNotice => null;
    }

    private class SearchOnlyFakeProvider : IContentProvider, IProviderSearch
    {
        public SearchOnlyFakeProvider(string id, string displayName, ProviderCapabilities declaredCapabilities)
        {
            Id = id;
            DisplayName = displayName;
            Capabilities = declaredCapabilities;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public ProviderCapabilities Capabilities { get; }
        public string? RightsNotice => null;

        public Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private class PlannerOnlyFakeProvider : IContentProvider, IProviderAcquisitionPlanner
    {
        public PlannerOnlyFakeProvider(string id, string displayName, ProviderCapabilities capabilities)
        {
            Id = id;
            DisplayName = displayName;
            Capabilities = capabilities;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public ProviderCapabilities Capabilities { get; }
        public string? RightsNotice => null;

        public Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(ProviderAcquisitionRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private class DownloadPolicyOnlyFakeProvider : IContentProvider, IProviderDownloadPolicy
    {
        public DownloadPolicyOnlyFakeProvider(string id, string displayName, ProviderCapabilities capabilities)
        {
            Id = id;
            DisplayName = displayName;
            Capabilities = capabilities;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public ProviderCapabilities Capabilities { get; }
        public string? RightsNotice => null;

        public IReadOnlyList<string> AllowedHosts => new[] { "archive.org" };
        public long MaxBytesPerPart => 100;
        public long MaxTotalBytes => 200;
        public int MaxParts => 1;
    }

    private class AcquisitionFakeProvider : IContentProvider, IProviderAcquisitionPlanner, IProviderDownloadPolicy
    {
        public AcquisitionFakeProvider(
            string id,
            string displayName,
            IReadOnlyList<string> allowedHosts,
            long maxBytesPerPart = 100,
            long maxTotalBytes = 200,
            int maxParts = 5)
        {
            Id = id;
            DisplayName = displayName;
            Capabilities = ProviderCapabilities.EbookAcquisition;
            AllowedHosts = allowedHosts;
            MaxBytesPerPart = maxBytesPerPart;
            MaxTotalBytes = maxTotalBytes;
            MaxParts = maxParts;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public ProviderCapabilities Capabilities { get; }
        public string? RightsNotice => null;

        public IReadOnlyList<string> AllowedHosts { get; }
        public long MaxBytesPerPart { get; }
        public long MaxTotalBytes { get; }
        public int MaxParts { get; }

        public Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(ProviderAcquisitionRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private class FullFeaturedFakeProvider : IContentProvider,
        IProviderSearch,
        IProviderCatalog,
        IProviderAcquisitionPlanner,
        IProviderDownloadPolicy
    {
        public FullFeaturedFakeProvider(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
            Capabilities = ProviderCapabilities.Search
                | ProviderCapabilities.ItemRetrieval
                | ProviderCapabilities.EbookAcquisition
                | ProviderCapabilities.AudiobookAcquisition
                | ProviderCapabilities.MultiTrackAudiobook;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public ProviderCapabilities Capabilities { get; }
        public string? RightsNotice => "Public Domain";

        public IReadOnlyList<string> AllowedHosts => new[] { "archive.org" };
        public long MaxBytesPerPart => 1000;
        public long MaxTotalBytes => 5000;
        public int MaxParts => 10;

        public Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(ProviderAcquisitionRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    #endregion
}
