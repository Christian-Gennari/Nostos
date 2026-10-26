using Nostos.Backend.Services.BookUpload;

namespace Nostos.Backend.Services;

/// <summary>
/// Expires abandoned resumable book uploads (#1266) and retires finished
/// session rows, deleting their staging bytes. A session that stops receiving
/// parts is not an error the owner must act on: the book row is unchanged and
/// picking the file again starts a fresh session.
/// </summary>
public sealed class BookUploadCleanupService(
    IServiceScopeFactory scopes,
    ILogger<BookUploadCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<BookUploadService>();
                var now = DateTime.UtcNow;
                var expired = await service.ExpireStaleAsync(now, stoppingToken);
                var purged = await service.PurgeTerminalAsync(
                    now - TerminalRetention,
                    stoppingToken);
                if (expired > 0 || purged > 0)
                {
                    logger.LogInformation(
                        "Book upload cleanup expired {Expired} session(s) and retired {Purged} finished session(s).",
                        expired,
                        purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A cleanup failure must never stop the host; the next pass
                // retries the same rows.
                logger.LogWarning(
                    exception,
                    "Book upload cleanup pass failed; the next pass retries.");
            }
        }
    }
}
