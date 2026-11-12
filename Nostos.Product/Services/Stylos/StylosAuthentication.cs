using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Nostos.Backend.Services.Stylos;

/// <summary>
/// <c>Authorization: Bearer stl_…</c> for the <c>/stylos/v1</c> endpoints. A
/// token names its device and that device's owner; the endpoints never take a
/// user id from the request. The scheme is not the host's default, so the rest
/// of the API keeps its own access model.
///
/// Failure messages never contain the token: ASP.NET logs them.
/// </summary>
public sealed class StylosDeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    StylosPairingService devices)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NostosStylosDevice";

    public const string OwnerClaimType = "nostos:stylos:owner";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
            return AuthenticateResult.NoResult();

        var header = values.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        var identity = await devices.AuthenticateAsync(token, Context.RequestAborted);
        if (identity is null)
            return AuthenticateResult.Fail("Invalid device token.");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, identity.DeviceId.ToString("D")),
                new Claim(ClaimTypes.Name, identity.Name),
                new Claim(OwnerClaimType, identity.OwnerKey),
            ],
            Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Response.WriteAsJsonAsync(
            new StylosErrorResponse("missing or invalid token"),
            Context.RequestAborted);
    }
}

/// <summary>SelfHosted wiring for the Stylos Sync Protocol.</summary>
public static class StylosRegistration
{
    /// <summary>The policy every <c>/stylos/v1</c> route requires.</summary>
    public const string Policy = "NostosStylosDevice";

    /// <summary>
    /// Registers the device store, the pairing service, the Bearer scheme and
    /// the policy. The host must also register an
    /// <see cref="IStylosOwnerProvider"/>; the endpoints and the Settings →
    /// Devices capability are mapped only when this method ran.
    /// </summary>
    public static IServiceCollection AddNostosStylosSync(this IServiceCollection services)
    {
        services.AddOptions<StylosOptions>().BindConfiguration(StylosOptions.SectionName);
        services.AddSingleton<StylosPairingService>();
        services.AddSingleton<StylosSyncService>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, StylosDeviceAuthenticationHandler>(
                StylosDeviceAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            Policy,
            policy => policy
                .AddAuthenticationSchemes(StylosDeviceAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()));
        return services;
    }
}
