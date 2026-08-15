using System.Collections.Concurrent;
using VeSessionManager.Core.Navigation;

namespace VeSessionManager.Web;

/// <summary>
/// Caches <see cref="NavBadgeCountService"/>'s counts for the app nav (_AppLayout.cshtml), which
/// renders on **every authenticated page request** and was issuing four uncached <c>CountAsync</c>
/// calls each time (#291). The expensive one is the VEC-submission count: a correlated
/// <c>Candidates.Any(...)</c> per session.
///
/// <para>Singleton, so the cache is shared across requests — which is the entire point — and
/// therefore a scoped <c>AppDbContext</c> cannot be injected here. A fresh scope is created per
/// refresh instead. Directly modelled on <see cref="IngestionHealthCache"/>, the sibling banner on
/// the same layout that was cached for exactly this reason.</para>
///
/// <para><b>Keyed by the team-id set</b>, because the counts are scoped to whoever is looking: null
/// means "every team" (a SystemAdmin) and is a genuinely different answer from any concrete list.
/// The number of distinct keys is bounded by the distinct team memberships across users, which on
/// this deployment is a handful.</para>
///
/// <para><b>The cost, stated plainly.</b> <see cref="NavBadgeCountService"/>'s own doc says a badge
/// and the list it corresponds to "can never disagree" — that is about the two sharing one filter
/// definition, and it stays true. What a TTL adds is a window where the badge is *stale*: resolve
/// the last unmatched payment and the nav can keep claiming 1 for up to
/// <see cref="CacheDuration"/> while the page beneath it correctly shows none. Thirty seconds is
/// chosen to keep that window shorter than the time it takes to notice, since these badges are a
/// "there is work outstanding" hint rather than a figure anyone reconciles against. If it ever does
/// grate, the fix is invalidation on the write paths, not a longer guess.</para>
/// </summary>
public class NavBadgeCountCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private sealed record Entry(NavBadgeCounts Counts, DateTimeOffset CachedAtUtc);

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// Null and empty must not collapse to the same key: null is "every team" (SystemAdmin) while
    /// empty is "no teams at all", and they produce opposite answers. Sorted so two callers with the
    /// same teams in a different order share one entry.
    /// </summary>
    private static string KeyFor(IReadOnlyList<int>? teamIds) =>
        teamIds is null ? "*" : string.Join(",", teamIds.OrderBy(id => id));

    public async Task<NavBadgeCounts> GetAsync(IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var key = KeyFor(teamIds);
        var now = timeProvider.GetUtcNow();

        if (entries.TryGetValue(key, out var hit) && now - hit.CachedAtUtc < CacheDuration)
        {
            return hit.Counts;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate: several concurrent requests can arrive at an expired entry
            // together, and only the first should do the work.
            now = timeProvider.GetUtcNow();
            if (entries.TryGetValue(key, out hit) && now - hit.CachedAtUtc < CacheDuration)
            {
                return hit.Counts;
            }

            using var scope = scopeFactory.CreateScope();
            var countService = scope.ServiceProvider.GetRequiredService<NavBadgeCountService>();
            var counts = await countService.GetCountsAsync(teamIds, cancellationToken);

            entries[key] = new Entry(counts, now);
            return counts;
        }
        finally
        {
            gate.Release();
        }
    }
}
