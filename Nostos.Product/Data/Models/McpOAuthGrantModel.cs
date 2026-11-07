namespace Nostos.Backend.Data.Models;

/// <summary>
/// One AI app the SelfHosted owner let in through OAuth (issue #1470): a
/// claude.ai or ChatGPT connector that came through the public address and was
/// approved in Settings → Connected apps. It is one connected app; its tokens
/// are <see cref="McpOAuthTokenModel"/> rows, and revoking the grant deletes
/// them all, so the next refresh and the next <c>/mcp</c> call both fail.
///
/// Host-local credentials, never part of a portable archive; Cloud does not
/// use the table (Clerk is its authorization server).
/// </summary>
public class McpOAuthGrantModel
{
    public const int MaxClientIdLength = 2048;
    public const int MaxClientNameLength = 200;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The client's metadata document URL (CIMD): its OAuth client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The <c>client_name</c> its metadata document gave when it was approved.</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary><see cref="McpAccessTokenModel.ReadScope"/> or <see cref="McpAccessTokenModel.ReadWriteScope"/>.</summary>
    public string Scope { get; set; } = McpAccessTokenModel.ReadScope;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the app last called <c>/mcp</c>; written at most once a minute.</summary>
    public DateTime? LastUsedAtUtc { get; set; }

    public List<McpOAuthTokenModel> Tokens { get; set; } = [];
}

/// <summary>
/// An access or refresh token of an <see cref="McpOAuthGrantModel"/>. Only the
/// SHA-256 of the token is stored. A refresh token is used once: using it
/// sets <see cref="ConsumedAtUtc"/>, and presenting it again revokes the grant.
/// </summary>
public class McpOAuthTokenModel
{
    public const string AccessKind = "access";
    public const string RefreshKind = "refresh";

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GrantId { get; set; }

    public McpOAuthGrantModel? Grant { get; set; }

    /// <summary><see cref="AccessKind"/> or <see cref="RefreshKind"/>.</summary>
    public string Kind { get; set; } = AccessKind;

    /// <summary>Lowercase hex SHA-256 of the whole token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When a refresh token was exchanged; null while it is still unused.</summary>
    public DateTime? ConsumedAtUtc { get; set; }
}
