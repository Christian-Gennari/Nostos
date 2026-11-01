namespace Nostos.Backend.Data;

/// <summary>
/// Which library revisions one write request caused, so the response can name
/// them (<see cref="HeaderName"/>). The change feed announces every revision to
/// every open tab, the tab that made the write included; a tab that knows which
/// revisions are its own can tell "my edit" from "someone else's change"
/// exactly, instead of guessing from timing.
///
/// <para>The request opens a receipt (<see cref="Begin"/>); a transaction that
/// advanced the revision records into it when it commits
/// (<see cref="LibraryChangeCommitInterceptor"/>). The receipt rides an
/// <see cref="AsyncLocal{T}"/> holder, so no database code knows about HTTP and
/// a commit outside a request records nowhere.</para>
/// </summary>
public static class LibraryWriteReceipt
{
    /// <summary>Response header: comma-separated revisions the request committed, oldest first.</summary>
    public const string HeaderName = "X-Library-Revisions";

    /// <summary>A request that commits more than this many times is reported as unknown (no header).</summary>
    private const int MaxRevisions = 32;

    private static readonly AsyncLocal<Receipt?> Current = new();

    /// <summary>Starts a receipt for the request flowing from here.</summary>
    public static Receipt Begin()
    {
        var receipt = new Receipt();
        Current.Value = receipt;
        return receipt;
    }

    /// <summary>Notes a committed revision on the receipt of the request this commit belongs to, if any.</summary>
    internal static void Record(string? revision)
    {
        if (revision is null)
            return;

        Current.Value?.Add(revision);
    }

    public sealed class Receipt
    {
        private readonly List<string> _revisions = [];
        private bool _overflowed;

        internal void Add(string revision)
        {
            lock (_revisions)
            {
                if (_revisions.Count >= MaxRevisions)
                    _overflowed = true;
                else
                    _revisions.Add(revision);
            }
        }

        /// <summary>The header value, or null when the request committed nothing (or too much to list).</summary>
        public string? HeaderValue
        {
            get
            {
                lock (_revisions)
                    return _overflowed || _revisions.Count == 0 ? null : string.Join(',', _revisions);
            }
        }
    }
}
