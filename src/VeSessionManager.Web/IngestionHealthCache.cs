using VeSessionManager.Core.Ingestion;

namespace VeSessionManager.Web;

/// <summary>
/// Caches <see cref="IngestionStatusService"/>'s report for the site-wide Worker-health banner
/// (_IngestionHealthBanner.cshtml), which renders on **every page request** for SystemAdmin and
/// TeamAdmin users. Without this, every navigation would pay for three extra queries to answer a
/// question whose answer changes at most once per polling interval.
///
/// Registered as a singleton, so the cache is shared across requests — which is the entire point.
/// The consequence is that <see cref="IngestionStatusService"/> (scoped, holding a scoped
/// AppDbContext) cannot be injected here directly; a fresh scope is created per refresh instead.
/// Injecting a scoped DbContext into a singleton is the classic way to end up with one context
/// living for the lifetime of the process.
///
/// A stale-by-up-to-a-minute answer is fine here: the condition being reported is "nothing has
/// happened for at least two polling intervals", i.e. at least two hours by default. A banner that
/// takes an extra minute to appear or clear is not meaningfully worse, and the Team Maintenance page
/// itself always reads live rather than through this cache.
/// </summary>
public class IngestionHealthCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim gate = new(1, 1);
    private IngestionStatusReport? cached;
    private DateTimeOffset cachedAtUtc = DateTimeOffset.MinValue;

    public async Task<IngestionStatusReport?> GetAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (cached is not null && now - cachedAtUtc < CacheDuration)
        {
            return cached;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate: several concurrent requests can arrive at an expired entry
            // together, and only the first should do the work.
            now = timeProvider.GetUtcNow();
            if (cached is not null && now - cachedAtUtc < CacheDuration)
            {
                return cached;
            }

            using var scope = scopeFactory.CreateScope();
            var statusService = scope.ServiceProvider.GetRequiredService<IngestionStatusService>();

            // teamIds: null — health is a deployment-wide question, deliberately evaluated across
            // every team regardless of who is looking. The per-team rows in the report are ignored
            // by the banner; only Health/NewestLastRunUtc/StaleAfter are read.
            cached = await statusService.GetAsync(null, cancellationToken);
            cachedAtUtc = now;
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }
}
