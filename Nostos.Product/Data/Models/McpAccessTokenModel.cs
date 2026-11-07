namespace Nostos.Backend.Data.Models;

/// <summary>
/// One access token a SelfHosted owner made in Settings → Connected apps so a
/// local AI app (Claude Code, Cursor, Claude Desktop) can call <c>/mcp</c>
/// (issue #1469).
///
/// Only the SHA-256 hash of the token is kept: the token itself is shown once
/// when it is made and cannot be read back. Revoking deletes the row, so the
/// next call with that token is refused. Host-local credentials, never part of
/// a portable archive; Cloud does not use the table (it signs apps in with
/// OAuth instead).
/// </summary>
public class McpAccessTokenModel
{
    public const int MaxNameLength = 80;

    /// <summary>Lowercase hex SHA-256 of the whole token, <c>nst_</c> prefix included.</summary>
    public const int HashLength = 64;

    public const string ReadScope = Nostos.Backend.Integrations.Mcp.McpConnectedAppScope.Read;
    public const string ReadWriteScope = Nostos.Backend.Integrations.Mcp.McpConnectedAppScope.ReadWrite;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the owner called it, so they know which app to revoke.</summary>
    public string Name { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    /// <summary><see cref="ReadScope"/> or <see cref="ReadWriteScope"/>.</summary>
    public string Scope { get; set; } = ReadScope;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When an app last called with it. Written at most once a minute per
    /// token, so a busy agent does not write on every request.
    /// </summary>
    public DateTime? LastUsedAtUtc { get; set; }
}
