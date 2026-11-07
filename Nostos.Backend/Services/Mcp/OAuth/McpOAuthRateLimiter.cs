using System.Threading.RateLimiting;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>
/// Per-address limits on the public OAuth endpoints (issue #1470), in memory
/// like the access token throttle: enough for an app and a waiting page that
/// polls every two seconds, not enough to fish for codes or tokens.
/// </summary>
public sealed class McpOAuthRateLimiter : IDisposable
{
    public enum Endpoint
    {
        Authorize,
        Status,
        Token,
        Revoke,
    }

    public static int PermitsPerMinute(Endpoint endpoint) => endpoint switch
    {
        Endpoint.Authorize => 20,
        Endpoint.Status => 120,
        _ => 60,
    };

    private readonly PartitionedRateLimiter<(Endpoint, string)> _limiter = PartitionedRateLimiter.Create<(Endpoint, string), (Endpoint, string)>(
        key => RateLimitPartition.GetFixedWindowLimiter(key, k => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitsPerMinute(k.Item1),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    public bool TryAcquire(Endpoint endpoint, HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "-";
        using var lease = _limiter.AttemptAcquire((endpoint, ip));
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
