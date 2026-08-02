namespace KartOrderService.Application.Common.Interfaces;

/// <summary>
/// BRD §24.3 audit-actor resolution + BRD §24.1.4 row-level-security principal — the same resolved
/// value both `Kart.Shared.Auditing`'s `created_by`/`updated_by` stamping and this service's own
/// `SET LOCAL app.current_principal[_kind]` RLS plumbing read from one place (database-design.md's
/// RLS section: "read one ambient current-principal accessor... not two independently-maintained
/// notions of who is acting"). Passed explicitly into domain factory/mutator methods and the
/// `IUnitOfWork` transaction-scope helper — never bound from a request DTO. `Kind` is one of
/// `"user"`, `"service"`, or `"system"` (database-design.md's RLS policy `IN ('service','system')` branch).
/// </summary>
public interface ICurrentPrincipal
{
    string ActingPrincipal { get; }

    string Kind { get; }
}
