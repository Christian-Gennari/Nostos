using System.Security.Claims;
using FluentAssertions;
using Nostos.Backend.Integrations.Mcp;
using Xunit;

namespace Nostos.Backend.Tests.Mcp;

/// <summary>
/// The host-neutral scope check (#1469): SelfHosted tokens and Cloud's Clerk
/// OAuth tokens (#557) both arrive as one space-separated <c>scope</c> claim.
/// </summary>
public sealed class McpScopesTests
{
    private static ClaimsPrincipal With(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void A_Clerk_access_token_with_both_scopes_may_read_and_write()
    {
        var user = With(("scope", "openid profile email offline_access nostos:read nostos:write"));

        McpScopes.CanRead(user).Should().BeTrue();
        McpScopes.CanWrite(user).Should().BeTrue();
    }

    [Fact]
    public void Read_alone_may_only_read()
    {
        var user = With(("scope", "openid nostos:read"));

        McpScopes.CanRead(user).Should().BeTrue();
        McpScopes.CanWrite(user).Should().BeFalse();
    }

    [Fact]
    public void A_scope_claim_without_nostos_read_may_do_nothing_even_with_write()
    {
        var user = With(("scope", "openid profile nostos:write"));

        McpScopes.CanRead(user).Should().BeFalse();
        McpScopes.CanWrite(user).Should().BeFalse();
    }

    [Fact]
    public void No_scope_claim_at_all_is_read_only()
    {
        var user = With((ClaimTypes.Name, "agent"));

        McpScopes.CanRead(user).Should().BeTrue();
        McpScopes.CanWrite(user).Should().BeFalse();
        McpScopes.CanWrite(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("nostos:writer nostos:read")]
    [InlineData("NOSTOS:WRITE nostos:read")]
    [InlineData("nostos:read,nostos:write")]
    public void Scopes_match_whole_and_ordinal(string scope)
    {
        McpScopes.CanWrite(With(("scope", scope))).Should().BeFalse();
    }

    [Fact]
    public void Scopes_split_over_several_claims_or_the_mapped_claim_type_count()
    {
        McpScopes.CanWrite(With(("scope", "nostos:read"), ("scope", "nostos:write"))).Should().BeTrue();
        McpScopes.CanWrite(With((McpScopes.MappedClaimType, "nostos:read nostos:write"))).Should().BeTrue();
    }
}
