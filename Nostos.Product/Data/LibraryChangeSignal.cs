using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nostos.Backend.Data;

/// <summary>
/// What a committed change to the library revision was.
/// <paramref name="ProgressOnly"/> is true when every portable change in the
/// transaction was reading position alone (location, percentage, last read),
/// which open screens may pick up lazily.
/// </summary>
public readonly record struct LibraryChange(bool ProgressOnly);

/// <summary>
/// Process-local wake-up for the library change feed (<c>GET /api/library/changes</c>).
///
/// <para>A transaction that advanced <see cref="Models.LibraryState.StateVersion"/>
/// publishes here when it commits (<see cref="LibraryChangeCommitInterceptor"/>),
/// so a feed waits instead of re-reading the revision every second. The signal
/// carries no data: a woken feed re-reads its own revision through its own
/// request's context factory, exactly as before.</para>
///
/// <para>Channels are keyed by database (host, port, database name or file), so a
/// write to one Cloud account's database never wakes another account's feed.
/// Writes this process cannot see — another instance, a restored or activated
/// database file — are caught by the feed's slow fallback re-read.</para>
/// </summary>
public static class LibraryChangeSignal
{
    private sealed class Channel
    {
        public TaskCompletionSource<LibraryChange> Next = NewSource();
    }

    private static readonly ConcurrentDictionary<string, Channel> Channels = new(StringComparer.Ordinal);

    /// <summary>
    /// Completes with the next change committed to the database behind
    /// <paramref name="key"/>. Take it before reading the revision, so a change
    /// that commits in between is not missed.
    /// </summary>
    public static Task<LibraryChange> NextAsync(string key) =>
        Volatile.Read(ref Channels.GetOrAdd(key, _ => new Channel()).Next).Task;

    /// <summary>Wakes every feed waiting on the database behind <paramref name="key"/>.</summary>
    public static void Publish(string key, LibraryChange change)
    {
        if (!Channels.TryGetValue(key, out var channel))
            return;

        Interlocked.Exchange(ref channel.Next, NewSource()).TrySetResult(change);
    }

    /// <summary>The channel key of the database <paramref name="db"/> talks to.</summary>
    public static string KeyFor(DbContext db) => KeyFor(db.Database.GetDbConnection());

    /// <summary>
    /// Built from the connection string's location parts only, which stay the
    /// same whether the connection is open or closed (a provider may drop the
    /// password from <see cref="DbConnection.ConnectionString"/> once open).
    /// </summary>
    public static string KeyFor(DbConnection connection)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };

        string Part(params string[] names)
        {
            foreach (var name in names)
            {
                if (builder.TryGetValue(name, out var value) && Convert.ToString(value) is { Length: > 0 } text)
                    return text;
            }

            return string.Empty;
        }

        return string.Join(
            '|',
            Part("Host", "Server", "Data Source", "DataSource", "Filename"),
            Part("Port"),
            Part("Database", "Initial Catalog"));
    }

    private static TaskCompletionSource<LibraryChange> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Publishes to <see cref="LibraryChangeSignal"/> when a transaction that
/// advanced the library revision commits — not before, so a woken feed always
/// reads the new value.
/// </summary>
public sealed class LibraryChangeCommitInterceptor : DbTransactionInterceptor
{
    public static readonly LibraryChangeCommitInterceptor Instance = new();

    private LibraryChangeCommitInterceptor()
    {
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Announce(eventData);
        base.TransactionCommitted(transaction, eventData);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Announce(eventData);
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    private static void Announce(TransactionEndEventData eventData)
    {
        if (eventData.Context is NostosDbContext db
            && db.TryTakeCommittedLibraryChange(eventData.TransactionId, out var change))
        {
            LibraryChangeSignal.Publish(LibraryChangeSignal.KeyFor(db), change);
        }
    }
}
