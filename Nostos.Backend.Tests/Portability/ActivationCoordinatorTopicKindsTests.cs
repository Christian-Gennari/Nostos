using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorTopicKindsTests
{
    [Fact]
    public async Task Activation_round_trips_library_with_12_custom_kinds_and_no_builtins()
    {
        var customKinds = Enumerable.Range(1, TopicKindModel.MaxKinds)
            .Select(i => new TopicKindModel
            {
                Id = Guid.NewGuid(),
                Name = $"Custom Kind {i:D2}",
                ColorKey = TopicKindModel.PaletteColorKeys[i - 1],
                SortOrder = i,
                CreatedAt = new DateTime(2026, 4, 1, 0, i, 0, DateTimeKind.Utc),
            })
            .ToArray();
        var secondTopicId = Guid.NewGuid();
        var thirdTopicId = Guid.NewGuid();

        await using var bed = await ActivationCoordinatorTestBed.CreateCustomAsync(
            populated: true,
            configureSource: async (source, ids) =>
            {
                var seeded = await source.Db.TopicKinds.ToListAsync();
                source.Db.TopicKinds.RemoveRange(seeded);
                await source.Db.SaveChangesAsync();

                source.Db.TopicKinds.AddRange(customKinds);
                var primaryTopic = await source.Db.Topics.SingleAsync(t => t.Id == ids.TopicId);
                primaryTopic.KindId = customKinds[0].Id;
                primaryTopic.KindSource = TopicKindSources.Reader;

                source.Db.Topics.AddRange(
                    new TopicModel
                    {
                        Id = secondTopicId,
                        Topic = "Second Custom Kind Topic",
                        KindId = customKinds[5].Id,
                        KindSource = TopicKindSources.Assistant,
                    },
                    new TopicModel
                    {
                        Id = thirdTopicId,
                        Topic = "Twelfth Custom Kind Topic",
                        KindId = customKinds[11].Id,
                        KindSource = TopicKindSources.ConnectedApp,
                    });
                await source.Db.SaveChangesAsync();
            });

        var result = await bed.ActivateAsync(confirm: true);

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        result.State.Should().Be(MigrationJobState.Completed);
        await bed.AssertImportedGenerationAsync();

        await using var scope = bed.Host.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var activeKinds = await db.TopicKinds.AsNoTracking().OrderBy(k => k.SortOrder).ToListAsync();
        activeKinds.Should().HaveCount(TopicKindModel.MaxKinds);

        var builtInIds = TopicKindModel.CreateBuiltInSeeds().Select(k => k.Id).ToHashSet();
        activeKinds.Should().NotContain(k => builtInIds.Contains(k.Id));
        activeKinds.Select(k => (k.Id, k.Name, k.ColorKey, k.SortOrder))
            .Should().Equal(customKinds.Select(k => (k.Id, k.Name, k.ColorKey, k.SortOrder)));

        var activeTopics = await db.Topics.AsNoTracking().ToListAsync();
        var t1 = activeTopics.Single(t => t.Id == bed.SourceIds.TopicId);
        t1.KindId.Should().Be(customKinds[0].Id);
        t1.KindSource.Should().Be(TopicKindSources.Reader);

        var t2 = activeTopics.Single(t => t.Id == secondTopicId);
        t2.KindId.Should().Be(customKinds[5].Id);
        t2.KindSource.Should().Be(TopicKindSources.Assistant);

        var t3 = activeTopics.Single(t => t.Id == thirdTopicId);
        t3.KindId.Should().Be(customKinds[11].Id);
        t3.KindSource.Should().Be(TopicKindSources.ConnectedApp);
    }

    [Fact]
    public async Task Activation_trims_whitespace_topic_kind_name_and_passes_candidate_and_post_activation_verification()
    {
        var kindId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        await using var bed = await ActivationCoordinatorTestBed.CreateCustomAsync(
            populated: false,
            transformArchive: archiveBytes => MutateArchiveAsync(
                archiveBytes,
                dataRoot =>
                {
                    dataRoot["topicKinds"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = kindId,
                            ["name"] = " Thinker ",
                            ["colorKey"] = "kind-1",
                            ["sortOrder"] = 1,
                            ["createdAt"] = DateTime.UnixEpoch,
                        },
                    };
                    dataRoot["concepts"]!.AsArray().Add(new JsonObject
                    {
                        ["id"] = topicId,
                        ["concept"] = "Kant",
                        ["kindId"] = kindId,
                        ["kindSource"] = TopicKindSources.Reader,
                    });
                },
                manifestRoot =>
                {
                    var counts = manifestRoot["counts"]!.AsObject();
                    counts["topics"] = counts["topics"]!.GetValue<int>() + 1;
                }));

        var result = await bed.ActivateAsync(confirm: true);

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        result.State.Should().Be(MigrationJobState.Completed);

        await using var scope = bed.Host.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var kind = await db.TopicKinds.AsNoTracking().SingleAsync();
        kind.Id.Should().Be(kindId);
        kind.Name.Should().Be("Thinker");

        var topic = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == topicId);
        topic.KindId.Should().Be(kindId);
        topic.KindSource.Should().Be(TopicKindSources.Reader);
    }

    private static async Task<byte[]> MutateArchiveAsync(
        byte[] archiveBytes,
        Action<JsonObject> mutateData,
        Action<JsonObject>? mutateManifest = null)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var input = new MemoryStream(archiveBytes))
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                await using var entryStream = entry.Open();
                using var copy = new MemoryStream();
                await entryStream.CopyToAsync(copy);
                entries[entry.FullName] = copy.ToArray();
            }
        }

        var dataNode = JsonNode.Parse(entries[PortableArchiveFormat.DataPath])!.AsObject();
        mutateData(dataNode);
        entries[PortableArchiveFormat.DataPath] = JsonSerializer.SerializeToUtf8Bytes(
            dataNode,
            new JsonSerializerOptions { WriteIndented = false });

        var manifestNode = JsonNode.Parse(entries[PortableArchiveFormat.ManifestPath])!.AsObject();
        mutateManifest?.Invoke(manifestNode);
        var dataBytes = entries[PortableArchiveFormat.DataPath];
        var dataSection = manifestNode["data"]!.AsObject();
        dataSection["length"] = dataBytes.LongLength;
        dataSection["sha256"] = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant();
        entries[PortableArchiveFormat.ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(
            manifestNode,
            new JsonSerializerOptions { WriteIndented = false });

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, payload) in entries)
            {
                var created = zip.CreateEntry(name, CompressionLevel.NoCompression);
                await using var dest = created.Open();
                await dest.WriteAsync(payload);
            }
        }

        return output.ToArray();
    }
}
