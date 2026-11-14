using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// <c>Authorization: Bearer crl_…</c> for the <c>/carrel/v1</c> endpoints. A
/// token names its device and that device's owner; the endpoints never take a
/// user id from the request. The scheme is not the host's default, so the rest
/// of the API keeps its own access model. Tokens issued before the rename
/// (<c>stl_…</c>) authenticate the same way.
///
/// Failure messages never contain the token: ASP.NET logs them.
/// </summary>
public sealed class CarrelDeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CarrelPairingService devices)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NostosCarrelDevice";

    public const string OwnerClaimType = "nostos:carrel:owner";

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
            new CarrelErrorResponse("missing or invalid token"),
            Context.RequestAborted);
    }
}

/// <summary>SelfHosted wiring for the Carrel Sync Protocol.</summary>
public static class CarrelRegistration
{
    /// <summary>The policy every <c>/carrel/v1</c> route requires.</summary>
    public const string Policy = "NostosCarrelDevice";

    /// <summary>
    /// Registers the device store, the pairing service, the Bearer scheme and
    /// the policy. The host must also register an
    /// <see cref="ICarrelOwnerProvider"/>; the endpoints and the Settings →
    /// Devices capability are mapped only when this method ran.
    ///
    /// <para>A host that keeps each owner's data in its own database registers
    /// its own <see cref="ICarrelDeviceDirectory"/> first; otherwise the
    /// single-database directory is used, which is the SelfHosted behaviour.
    /// The directory is resolved by singleton services, so a custom one must
    /// not depend on scoped services.</para>
    /// </summary>
    public static IServiceCollection AddNostosCarrelSync(this IServiceCollection services)
    {
        services.AddOptions<CarrelOptions>().BindConfiguration(CarrelOptions.SectionName);
        services.TryAddSingleton<ICarrelDeviceDirectory, SingleDatabaseCarrelDeviceDirectory>();
        services.AddSingleton<CarrelPairingService>();
        services.AddSingleton<CarrelSyncService>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, CarrelDeviceAuthenticationHandler>(
                CarrelDeviceAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            Policy,
            policy => policy
                .AddAuthenticationSchemes(CarrelDeviceAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()));
        return services;
    }
}
