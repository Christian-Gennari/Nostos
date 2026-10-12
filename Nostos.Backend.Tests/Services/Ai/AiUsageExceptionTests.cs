using FluentAssertions;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// The code and status are a public contract: the assistant, transcription and
/// alignment endpoints all answer a usage block with them, and clients switch
/// on the code.
/// </summary>
public sealed class AiUsageExceptionTests
{
    [Theory]
    [InlineData(AiUsageBlockReason.AccessDenied, "ai_access_denied", 403)]
    [InlineData(AiUsageBlockReason.RateLimited, "ai_rate_limited", 429)]
    [InlineData(AiUsageBlockReason.LimitReached, "ai_usage_limit_reached", 429)]
    [InlineData(AiUsageBlockReason.Disabled, "ai_disabled", 503)]
    [InlineData(AiUsageBlockReason.TemporarilyUnavailable, "ai_temporarily_unavailable", 503)]
    public void Reports_each_reason_under_its_code_and_status(
        AiUsageBlockReason reason,
        string code,
        int statusCode)
    {
        var exception = new AiUsageException(reason, "blocked");

        exception.Code.Should().Be(code);
        exception.StatusCode.Should().Be(statusCode);
    }
}
