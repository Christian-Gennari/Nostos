using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class KoboSideloadAuthorTests
{
    [Theory]
    [InlineData("file:///mnt/onboard/Arthur%20Conan%20Doyle/Sherlock.epub", "Arthur Conan Doyle")]
    [InlineData("file:///mnt/onboard/Books/Sherlock.epub", null)]
    [InlineData("file:///mnt/onboard/Sherlock.epub", null)]
    [InlineData("file:///mnt/onboard/A/B/Sherlock.epub", null)]
    [InlineData("0f3c-uuid-volume", null)]
    public void Author_comes_from_the_folder_under_the_device_root(string volumeId, string? expected) =>
        KoboDatabaseReader.AuthorFromVolumeId(volumeId).Should().Be(expected);
}
