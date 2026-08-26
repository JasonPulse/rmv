using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Rmv.Web.Data;

/// <summary>
/// Reports readiness from <see cref="DatabaseState"/> rather than by probing the
/// database directly. Docker's healthcheck hits /healthz/ready, so this is what
/// decides whether the container is considered up, and therefore whether
/// cloudflared starts routing to it.
/// </summary>
public sealed class DatabaseHealthCheck(DatabaseState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(state.Status switch
        {
            // Nothing configured, so nothing to be unready about.
            DatabaseStatus.NotConfigured => HealthCheckResult.Healthy("no database configured"),
            DatabaseStatus.Ready => HealthCheckResult.Healthy("migrations applied"),
            DatabaseStatus.Starting => HealthCheckResult.Unhealthy("migrations not applied yet"),
            _ => HealthCheckResult.Unhealthy(state.Detail ?? "database unavailable"),
        });
}
