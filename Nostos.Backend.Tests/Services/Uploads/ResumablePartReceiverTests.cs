using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services.Uploads;
using Xunit;

namespace Nostos.Backend.Tests.Services.Uploads;

/// <summary>
/// The shared bounded receive (#1629): one 128 KiB budgeted buffer, a read that
/// stops one byte past the declared length, and a SHA-256 over exactly the
/// bytes written. Both resumable upload contracts depend on this, so its edges
/// are pinned here rather than only through the two endpoints.
/// </summary>
public sealed class ResumablePartReceiverTests
{
    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task An_exact_part_is_written_and_verified()
    {
        var bytes = Encoding.ASCII.GetBytes("the bytes of one part");
        using var output = new MemoryStream();

        var outcome = await ResumablePartReceiver.ReceiveAsync(
            new MemoryStream(bytes), output, bytes.Length, Sha256Hex(bytes), CancellationToken.None);

        outcome.Should().Be(ResumablePartOutcome.Received);
        output.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task A_short_body_is_a_range_failure()
    {
        var bytes = Encoding.ASCII.GetBytes("short");
        using var output = new MemoryStream();

        var outcome = await ResumablePartReceiver.ReceiveAsync(
            new MemoryStream(bytes), output, bytes.Length + 1, Sha256Hex(bytes), CancellationToken.None);

        outcome.Should().Be(ResumablePartOutcome.RangeInvalid);
    }

    [Fact]
    public async Task An_oversized_body_is_stopped_one_byte_past_the_declared_length()
    {
        var body = new byte[64 * 1024];
        new Random(1629).NextBytes(body);
        using var source = new MemoryStream(body);
        using var output = new MemoryStream();

        var outcome = await ResumablePartReceiver.ReceiveAsync(
            source, output, 1024, Sha256Hex(body.AsSpan(0, 1024)), CancellationToken.None);

        outcome.Should().Be(ResumablePartOutcome.RangeInvalid);
        source.Position.Should().Be(1025, "an oversized part is refused, not drained into storage");
    }

    [Fact]
    public async Task A_wrong_hash_is_a_hash_failure()
    {
        var bytes = Encoding.ASCII.GetBytes("checksum matters");
        using var output = new MemoryStream();

        var outcome = await ResumablePartReceiver.ReceiveAsync(
            new MemoryStream(bytes), output, bytes.Length, new string('0', 64), CancellationToken.None);

        outcome.Should().Be(ResumablePartOutcome.HashMismatch);
    }

    [Fact]
    public async Task A_received_part_leaves_its_temporary_file_for_the_caller_to_place()
    {
        var directory = Directory.CreateTempSubdirectory("nostos-part-receiver-").FullName;
        try
        {
            var path = Path.Combine(directory, "0.part");
            var bytes = Encoding.ASCII.GetBytes("kept");

            var outcome = await ResumablePartReceiver.ReceiveIntoAsync(
                () => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                () => File.Delete(path),
                new MemoryStream(bytes),
                bytes.Length,
                Sha256Hex(bytes),
                CancellationToken.None);

            outcome.Should().Be(ResumablePartOutcome.Received);
            File.Exists(path).Should().BeTrue();
            (await File.ReadAllBytesAsync(path)).Should().Equal(bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_refused_part_deletes_its_temporary_file()
    {
        var directory = Directory.CreateTempSubdirectory("nostos-part-receiver-").FullName;
        try
        {
            var path = Path.Combine(directory, "0.part");
            var bytes = Encoding.ASCII.GetBytes("refused");

            var outcome = await ResumablePartReceiver.ReceiveIntoAsync(
                () => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                () => File.Delete(path),
                new MemoryStream(bytes),
                bytes.Length,
                new string('0', 64),
                CancellationToken.None);

            outcome.Should().Be(ResumablePartOutcome.HashMismatch);
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_body_that_fails_mid_part_still_deletes_the_temporary_file()
    {
        var directory = Directory.CreateTempSubdirectory("nostos-part-receiver-").FullName;
        try
        {
            var path = Path.Combine(directory, "0.part");
            var content = new MemoryStream(Encoding.ASCII.GetBytes("half"));
            content.Dispose();

            var receive = () => ResumablePartReceiver.ReceiveIntoAsync(
                () => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                () => File.Delete(path),
                content,
                4,
                new string('0', 64),
                CancellationToken.None);

            await receive.Should().ThrowAsync<ObjectDisposedException>();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
