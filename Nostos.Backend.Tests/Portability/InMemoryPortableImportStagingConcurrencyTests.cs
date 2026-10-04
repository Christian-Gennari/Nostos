using System.Security.Cryptography;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Deterministic interleaving tests for <see cref="InMemoryPortableImportStaging"/>.
/// The hooks pause an operation before it takes its staging-area lock, so each race
/// resolution is forced rather than timed. No sleeps are used.
/// </summary>
public sealed class InMemoryPortableImportStagingConcurrencyTests
{
    [Fact]
    public async Task Delete_wins_when_completion_pauses_before_its_area_lock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staging = CreateStaging(new InMemoryPortableImportStagingHooks
        {
            BeforeCompleteAsync = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            },
        });
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var completion = Task.Run(() => staging.CompleteMediaAsync(id, write));

        await entered.Task;
        await staging.DeleteAsync(id);
        release.TrySetResult();

        var failure = await Assert.ThrowsAsync<PortableStagingException>(() => completion);
        failure.Code.Should().Be(PortableStagingException.NotFoundCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
        await staging.DeleteAsync(id);
        await write.DisposeAsync();
    }

    [Fact]
    public async Task Completion_wins_when_delete_pauses_before_its_area_lock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staging = CreateStaging(new InMemoryPortableImportStagingHooks
        {
            BeforeDeleteAsync = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            },
        });
        var id = await staging.CreateAsync();
        var content = new byte[] { 4, 5, 6 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var deletion = Task.Run(() => staging.DeleteAsync(id));

        await entered.Task;
        await staging.CompleteMediaAsync(id, write);
        release.TrySetResult();
        await deletion;

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
        await staging.DeleteAsync(id);
        await write.DisposeAsync();
    }

    [Fact]
    public async Task Delete_wins_when_a_stream_write_pauses_before_its_area_lock()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooks = new InMemoryPortableImportStagingHooks();
        var staging = CreateStaging(hooks);
        var id = await staging.CreateAsync();
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(new byte[8]));
        await write.Stream.WriteAsync(new byte[4]);

        hooks.BeforeStreamWrite = () =>
        {
            entered.Set();
            release.Wait();
        };

        var writing = Task.Run(async () =>
        {
            try
            {
                await write.Stream.WriteAsync(new byte[4]);
                return null;
            }
            catch (PortableStagingException exception)
            {
                return exception;
            }
        });

        entered.Wait();
        await staging.DeleteAsync(id);
        release.Set();

        var failure = await writing;
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(PortableStagingException.NotFoundCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write));
        await write.DisposeAsync();
    }

    [Fact]
    public async Task Two_concurrent_completions_of_the_same_handle_are_idempotent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        var staging = CreateStaging(new InMemoryPortableImportStagingHooks
        {
            BeforeCompleteAsync = async () =>
            {
                if (Interlocked.Increment(ref enteredCount) == 2)
                {
                    entered.TrySetResult();
                }

                await release.Task;
            },
        });
        var id = await staging.CreateAsync();
        var content = new byte[] { 7, 7, 7 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var first = Task.Run(() => staging.CompleteMediaAsync(id, write));
        var second = Task.Run(() => staging.CompleteMediaAsync(id, write));

        await entered.Task;
        release.TrySetResult();
        await Task.WhenAll(first, second);

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    private static InMemoryPortableImportStaging CreateStaging(InMemoryPortableImportStagingHooks hooks) =>
        new(new InMemoryPortableImportStagingStore(), hooks);

    private static PortableArchiveMediaEntry MediaDescriptor(byte[] content) =>
        new(
            BookId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Kind: "book",
            Path: "media/book.bin",
            FileName: "book.bin",
            ContentType: "application/octet-stream",
            Length: content.LongLength,
            Sha256: Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
}
