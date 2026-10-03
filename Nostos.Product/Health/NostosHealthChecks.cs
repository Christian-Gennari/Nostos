using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nostos.Backend.Data;

namespace Nostos.Backend.Health;

public static class NostosHealthCheckTags
{
    public const string Readiness = "ready";
}

public static class NostosHealthRegistration
{
    public static IServiceCollection AddNostosSelfHostedHealthChecks(
        this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<SelfHostedDatabaseReadinessCheck>(
                "sqlite",
                tags: [NostosHealthCheckTags.Readiness]);

        return services;
    }
}

public sealed class SelfHostedDatabaseReadinessCheck(
    IDbContextFactory<NostosDbContext> contexts) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("database_unavailable");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("database_unavailable");
        }
    }
}

public static class NostosHealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var status = report.Status == HealthStatus.Healthy ? "ok" : "unavailable";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new { status }),
            context.RequestAborted);
    }
}
