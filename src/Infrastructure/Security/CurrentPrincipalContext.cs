namespace KartOrderService.Infrastructure.Security;

/// <summary>
/// Lets a non-HTTP caller (a Saga-step event consumer, the reconciliation sweep, the outbox
/// poller/read-model projector) stamp the correct `system:*` audit actor + RLS principal-kind
/// (ddd-model.md's audit-actor invariant) on a call into a MediatR handler otherwise shared with an
/// authenticated HTTP path (e.g. `ResolveFulfillmentException`'s `retry` action re-transitions the
/// same order a Saga consumer also writes to). `AsyncLocal` scopes the override to the async call
/// chain a single message dispatch spans, never leaking across concurrently-processed messages.
/// </summary>
public static class CurrentPrincipalContext
{
    private static readonly AsyncLocal<(string Principal, string Kind)?> Ambient = new();

    public static (string Principal, string Kind)? Current => Ambient.Value;

    public static IDisposable SetScope(string principal, string kind)
    {
        Ambient.Value = (principal, kind);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }
}
