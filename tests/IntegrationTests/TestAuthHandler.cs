using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KartOrderService.IntegrationTests;

/// <summary>
/// Replaces the real JWT-bearer scheme in tests (no Identity/JWKS to talk to) - always
/// authenticates, deriving `roles` claims from the `X-Test-Roles` header a test sets
/// (comma-separated; defaults to "customer"). Mirrors `kart-payment-service`'s identically-shaped handler.
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rolesHeader = Request.Headers.TryGetValue("X-Test-Roles", out var value) ? value.ToString() : "customer";
        var userIdHeader = Request.Headers.TryGetValue("X-Test-User-Id", out var userId) ? userId.ToString() : Guid.NewGuid().ToString();

        var claims = new List<Claim> { new("sub", userIdHeader) };
        claims.AddRange(rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(role => new Claim("roles", role.Trim())));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
