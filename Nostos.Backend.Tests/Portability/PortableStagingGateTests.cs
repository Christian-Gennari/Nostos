using System.Security.Cryptography;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Characterization of the staging gate both staging providers share: concurrent
/// operations queue on one gate in the order they reach it, a wait that is cancelled
/// is refused as a cancellation and leaves the previous visible state intact, and no
/// operation publishes a partially visible item while another holds the gate. Every
/// assertion runs against each provider, because the gate is the only point where the
/// two state machines must agree.
/// </summary>
public sealed class PortableStagingGateTests : IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-staging-gate-{Guid.NewGuid():N}");

    public enum StagingProvider
    {
        File,
        Local,
    }

    [Theory]
    [InlineData(StagingProvider.File)]
    [InlineData(StagingProvider.Local)]
    public async Task Delete_wins_when_completion_pauses_before_the_gate(
        StagingProvider provider)
    {
        var gate = new PausableGate(PortableStagingOperation.CompleteMedia);
        await using var staging = CreateStaging(provider, gate);
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var completion = Task.Run(async () =>
        {
            try
            {
                await staging.CompleteMediaAsync(id, write);
                return null;
            }
            catch (PortableStagingException exception)
            {
                return exception;
            }
        });

        await gate.Entered.Task.WaitAsync(OperationTimeout);
        await staging.DeleteAsync(id).WaitAsync(OperationTimeout);
        gate.Release.TrySetResult();

        var failure = await completion.WaitAsync(OperationTimeout);
        failure.Should().NotBeNull("the delete took the gate first");
        failure!.Code.Should().Be(PortableStagingException.NotFoundCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        await write.DisposeAsync();
    }

    [Theory]
    [InlineData(StagingProvider.File)]
    [InlineData(StagingProvider.Local)]
    public async Task Completion_wins_when_delete_pauses_before_the_gate(
        StagingProvider provider)
    {
        var gate = new PausableGate(PortableStagingOperation.Delete);
        await using var staging = CreateStaging(provider, gate);
        var id = await staging.CreateAsync();
        var content = new byte[] { 4, 5, 6 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var deletion = Task.Run(() => staging.DeleteAsync(id));

        await gate.Entered.Task.WaitAsync(OperationTimeout);
        await staging.CompleteMediaAsync(id, write).WaitAsync(OperationTimeout);
        gate.Release.TrySetResult();
        await deletion.WaitAsync(OperationTimeout);

        // The delete still ran, so the completed item is not visible afterwards.
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
        await write.DisposeAsync();
    }

    [Theory]
    [InlineData(StagingProvider.File)]
    [InlineData(StagingProvider.Local)]
    public async Task A_queued_operation_does_not_hold_the_gate_for_the_one_ahead_of_it(
        StagingProvider provider)
    {
        var gate = new PausableGate(PortableStagingOperation.CompleteMedia);
        await using var staging = CreateStaging(provider, gate);
        var id = await staging.CreateAsync();
        var content = new byte[] { 7, 7 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var completion = Task.Run(() => staging.CompleteMediaAsync(id, write));
        await gate.Entered.Task.WaitAsync(OperationTimeout);

        // The paused operation is waiting outside the gate: another operation on the
        // same staging area runs to completion while it is paused.
        await staging.CreateAsync().WaitAsync(OperationTimeout);
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));

        gate.Release.TrySetResult();
        await completion.WaitAsync(OperationTimeout);
        (await staging.ListMediaAsync(id)).Should().ContainSingle();
        await write.DisposeAsync();
    }

    [Theory]
    [InlineData(StagingProvider.File)]
    [InlineData(StagingProvider.Local)]
    public async Task Concurrent_opens_of_one_media_descriptor_admit_exactly_one(
        StagingProvider provider)
    {
        await using var staging = CreateStaging(provider);
        var id = await staging.CreateAsync();
        var content = new byte[] { 8, 8, 8 };
        var descriptor = MediaDescriptor(content);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    return (object)await staging.OpenMediaWriteAsync(id, descriptor);
                }
                catch (PortableStagingException exception)
                {
                    return exception;
                }
            }))
            .ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts).WaitAsync(OperationTimeout);

        var winner = results.OfType<PortableStagingWrite>().Should().ContainSingle().Subject;
        results.OfType<PortableStagingException>()
            .Should().OnlyContain(exception => exception.Code == PortableStagingException.ConflictCode);

        await winner.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, winner);
        await winner.DisposeAsync();

        (await staging.ListMediaAsync(id)).Should().ContainSingle()
            .Which.Reference.Should().Be(winner.Reference);
    }

    [Theory]
    [InlineData(StagingProvider.File)]
    [InlineData(StagingProvider.Local)]
    public async Task A_cancelled_wait_is_refused_and_leaves_the_previous_state(
        StagingProvider provider)
    {
        var gate = new PausableGate(PortableStagingOperation.CompleteMedia);
        await using var staging = CreateStaging(provider, gate);
        var id = await staging.CreateAsync();
        var content = new byte[] { 9, 9, 9 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        using var cancelled = new CancellationTokenSource();
        var completion = Task.Run(
            () => staging.CompleteMediaAsync(id, write, cancelled.Token));

        await gate.Entered.Task.WaitAsync(OperationTimeout);
        await cancelled.CancelAsync();
        gate.Release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => completion.WaitAsync(OperationTimeout));

        // The cancelled transition published nothing, and the item is still completable.
        (await staging.ListMediaAsync(id)).Should().BeEmpty();
        await staging.CompleteMediaAsync(id, write);
        await write.DisposeAsync();
        (await staging.ListMediaAsync(id)).Should().ContainSingle();
    }

    /// <summary>
    /// The write stream's async path waits on the same gate as the transitions, so a
    /// write queued behind a completion is cancelled as a cancellation — not as a
    /// staging refusal — and publishes nothing. The durable provider can hold the gate
    /// from inside a real transition through its inventory seam; the process-local one
    /// has no in-gate seam, which is why this runs on the durable provider only.
    /// </summary>
    [Fact]
    public async Task An_async_write_cancelled_behind_the_gate_publishes_nothing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new FilePortableImportStagingHooks();
        await using var staging = new FilePortableImportStaging(
            new TransferPathResolver(Path.Combine(_root, "file-gated")),
            hooks,
            coordinatorScope: string.Empty);

        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        hooks.BeforeInventoryReplace = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var completion = Task.Run(() => staging.CompleteMediaAsync(id, write));
        await entered.Task.WaitAsync(OperationTimeout);

        using var cancelled = new CancellationTokenSource();
        var blocked = Task.Run(async () =>
            await write.Stream.WriteAsync(new byte[] { 9 }, cancelled.Token));
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocked.WaitAsync(OperationTimeout));

        release.TrySetResult();
        await completion.WaitAsync(OperationTimeout);

        (await staging.ListMediaAsync(id)).Should().ContainSingle();
        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);

        hooks.BeforeInventoryReplace = null;
        await write.DisposeAsync();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private IPortableImportStaging CreateStaging(
        StagingProvider provider,
        PausableGate? gate = null) => provider switch
        {
            StagingProvider.File => new FilePortableImportStaging(
                new TransferPathResolver(Path.Combine(_root, "file")),
                gate is null
                    ? null
                    : new FilePortableImportStagingHooks { BeforeAreaGateAsync = gate.BeforeGateAsync },
                coordinatorScope: string.Empty),
            StagingProvider.Local => new LocalPortableImportStaging(
                Path.Combine(_root, "local"),
                availableBytesProbe: null,
                gate is null
                    ? null
                    : new LocalPortableImportStagingHooks { BeforeAreaGateAsync = gate.BeforeGateAsync }),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static PortableArchiveMediaEntry MediaDescriptor(byte[] content) =>
        new(
            BookId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Kind: "book",
            Path: "media/book.bin",
            FileName: "book.bin",
            ContentType: "application/octet-stream",
            Length: content.LongLength,
            Sha256: Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    /// <summary>
    /// Pauses exactly one matching operation before it takes the gate. Later
    /// operations of the same kind pass straight through, so a test can retry after
    /// the paused one has been released.
    /// </summary>
    private sealed class PausableGate
    {
        private readonly PortableStagingOperation _operation;
        private int _paused;

        internal PausableGate(PortableStagingOperation operation) => _operation = operation;

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task BeforeGateAsync(PortableStagingOperation operation)
        {
            if (operation != _operation || Interlocked.Exchange(ref _paused, 1) != 0)
                return;

            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }
}
