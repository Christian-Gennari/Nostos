using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

// Slice 3 of issue #679, review fix 2: admission retries transient provider
// contention (SQLite busy/locked, PostgreSQL serialization/deadlock) and gives
// up with the last transient failure so the caller can surface a typed
// contention result. Provider detection is structural via DbException.SqlState,
// so the product assembly keeps no Npgsql dependency.
public sealed class TransferAdmissionRetryTests
{
    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    public void Transient_detection_recognizes_postgres_serialization_and_deadlock_sqlstates(
        string sqlState)
    {
        TransferAdmissionRetry.IsTransientContention(new FakeDbException(sqlState))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("23505")]
    [InlineData("42601")]
    [InlineData("57P01")]
    public void Transient_detection_ignores_non_retryable_sqlstates(string sqlState)
    {
        TransferAdmissionRetry.IsTransientContention(new FakeDbException(sqlState))
            .Should().BeFalse();
    }

    [Fact]
    public void Transient_detection_walks_wrapped_exception_chains()
    {
        var wrapped = new DbUpdateException(
            "save failed",
            new FakeDbException("40001"));

        TransferAdmissionRetry.IsTransientContention(wrapped).Should().BeTrue();
    }

    [Fact]
    public void Transient_detection_ignores_unrelated_failures()
    {
        TransferAdmissionRetry.IsTransientContention(new InvalidOperationException("nope"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Retry_policy_retries_transient_failures_then_succeeds()
    {
        var attempts = 0;

        var result = await TransferAdmissionRetry.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < TransferAdmissionRetry.MaxAttempts)
                {
                    throw new FakeDbException("40001");
                }

                return Task.FromResult(42);
            },
            onRetry: () => { },
            ct: default,
            delay: static (_, _) => Task.CompletedTask);

        result.Should().Be(42);
        attempts.Should().Be(TransferAdmissionRetry.MaxAttempts);
    }

    [Fact]
    public async Task Retry_policy_reraises_after_the_bounded_attempts_are_exhausted()
    {
        var attempts = 0;

        var act = () => TransferAdmissionRetry.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                throw new FakeDbException("40P01");
            },
            onRetry: () => { },
            ct: default,
            delay: static (_, _) => Task.CompletedTask);

        await act.Should().ThrowAsync<FakeDbException>();
        attempts.Should().Be(TransferAdmissionRetry.MaxAttempts);
    }

    [Fact]
    public async Task Retry_policy_reraises_non_transient_failures_immediately()
    {
        var attempts = 0;

        var act = () => TransferAdmissionRetry.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("permanent");
            },
            onRetry: () => { },
            ct: default,
            delay: static (_, _) => Task.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Retry_policy_calls_the_retry_callback_before_each_wait()
    {
        var attempts = 0;
        var resets = 0;

        var result = await TransferAdmissionRetry.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new FakeDbException("40001");
                }

                return Task.FromResult("ok");
            },
            onRetry: () => resets++,
            ct: default,
            delay: static (_, _) => Task.CompletedTask);

        result.Should().Be("ok");
        attempts.Should().Be(2);
        resets.Should().Be(1);
    }

    private sealed class FakeDbException(string sqlState) : DbException
    {
        public override string? SqlState => sqlState;
    }
}
