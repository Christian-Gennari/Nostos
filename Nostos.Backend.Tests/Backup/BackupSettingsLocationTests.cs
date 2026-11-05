using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Backup;

public sealed class BackupSettingsLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nostos-bsl-{Guid.NewGuid():N}");
    private string AppDir => Path.Combine(_root, "app");
    private string DataDir => Path.Combine(_root, "data");

    public BackupSettingsLocationTests() => Directory.CreateDirectory(AppDir);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private BackupSettingsProvider Create() => new(
        new Env { ContentRootPath = AppDir },
        Options.Create(new BackupSettings { IsEnabled = false, IntervalHours = 168 }),
        null,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:DatabasePath"] = Path.Combine(DataDir, "nostos.db"),
        }).Build());

    [Fact]
    public void Settings_are_stored_beside_the_database_and_survive_a_new_app_folder()
    {
        Create().Update(s => { s.IsEnabled = true; s.IntervalHours = 6; });

        File.Exists(Path.Combine(DataDir, "backup-settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(AppDir, "backup-settings.json")).Should().BeFalse();

        Directory.Delete(AppDir, true); // the container is recreated
        Directory.CreateDirectory(AppDir);
        var reloaded = Create().Current;
        reloaded.IsEnabled.Should().BeTrue();
        reloaded.IntervalHours.Should().Be(6);
    }

    [Fact]
    public void Existing_file_in_the_app_folder_is_moved_to_the_data_folder()
    {
        File.WriteAllText(Path.Combine(AppDir, "backup-settings.json"),
            "{\"isEnabled\":true,\"intervalHours\":6}");

        var provider = Create();

        provider.Current.IsEnabled.Should().BeTrue();
        provider.Current.IntervalHours.Should().Be(6);
        File.Exists(Path.Combine(DataDir, "backup-settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(AppDir, "backup-settings.json")).Should().BeFalse();
    }

    [Fact]
    public void Existing_file_in_the_data_folder_wins_over_the_old_one()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "backup-settings.json"), "{\"isEnabled\":true,\"intervalHours\":12}");
        File.WriteAllText(Path.Combine(AppDir, "backup-settings.json"), "{\"isEnabled\":false,\"intervalHours\":1}");

        Create().Current.IntervalHours.Should().Be(12);
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Nostos.Backend";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
