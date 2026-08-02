using System.IdentityModel.Tokens.Jwt;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using Microsoft.AspNetCore.Http;

namespace KartOrderService.Infrastructure.Security;

/// <summary>
/// Resolves the acting principal + RLS principal-kind from, in priority order: (1) an ambient
/// <see cref="CurrentPrincipalContext"/> override — set by a Saga consumer/sweep/poller before it
/// calls into a handler otherwise shared with an authenticated HTTP path; (2) the caller's
/// Identity-issued access token `sub` claim — the owning customer for `POST /orders`/`.../cancel`,
/// or Admin Service's client-credentials principal for `.../resolve-fulfillment-exception`; (3) a
/// well-known "unknown" system id as the final fallback.
/// </summary>
public sealed class HttpCurrentPrincipal(IHttpContextAccessor httpContextAccessor) : ICurrentPrincipal
{
    private const string RolesClaimType = "roles";

    public string ActingPrincipal =>
        CurrentPrincipalContext.Current?.Principal
        ?? httpContextAccessor.HttpContext?.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? "system:unknown";

    public string Kind =>
        CurrentPrincipalContext.Current?.Kind
        ?? (httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true ? ResolveHttpKind() : "system");

    /// <summary>database-design.md's RLS policy `IN ('service','system')` branch: Admin Service's client-credentials principal (carrying an `admin` role claim) is `"service"`; every other authenticated caller is the owning customer, `"user"`.</summary>
    private string ResolveHttpKind()
    {
        var roles = httpContextAccessor.HttpContext?.User?.FindAll(RolesClaimType).Select(c => c.Value) ?? [];
        return roles.Contains("admin") ? "service" : "user";
    }
}
