using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace KartOrderService.Api.Security;

/// <summary>
/// `api-contract.yaml`'s `bearerAuth` (customer, implicit on `POST /orders`/`GET /orders/{id}`/
/// `.../cancel`) and `clientCredentials` (Admin Service, `admin` scope, on
/// `.../resolve-fulfillment-exception`) schemes — both are the same Identity-issued, JWKS-verified
/// JWT bearer token, distinguished only by a `roles` claim value, matching
/// `kart-payment-service/Security/AuthenticationExtensions.cs`'s reasoning for not standing up a
/// second authentication scheme purely for documentation symmetry (coding-standards.md's
/// anti-pattern check).
/// </summary>
public static class AuthenticationExtensions
{
    public const string AdminOnlyPolicy = "AdminOnly";
    private const string RolesClaimType = "roles";

    public static IServiceCollection AddOrderAuthentication(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddHttpClient<JwksSigningKeyResolver>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwksSigningKeyResolver>((options, resolver) =>
            {
                // Defensively disable .NET's default inbound claim-type remapping so the raw "roles"
                // claim name survives verbatim (the AdminOnly policy below matches on it literally) —
                // the same claim-remapping gap closed in kart-category-service and other services.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = resolver.ResolveSigningKeys,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminOnlyPolicy, policy => policy.RequireClaim(RolesClaimType, "admin"));

        return services;
    }
}
