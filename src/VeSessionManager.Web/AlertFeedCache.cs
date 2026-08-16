using System.Collections.Concurrent;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;

namespace VeSessionManager.Web;

/// <summary>
/// Caches <see cref="AlertFeedService"/>'s feed for the nav's alert bell (_AlertBell.cshtml), which
/// renders on every authenticated page request. Third cache of this exact shape on this layout, after
/// <see cref="IngestionHealthCache"/> and <see cref="NavBadgeCountCache"/> — see the latter for the
/// singleton-plus-fresh-scope reasoning, which is the same here.
///
/// <para><b>Keyed by role as well as teams</b>, unlike the badge cache: the feed is role-gated at
/// source (an admin-only alert must not reach a SessionManager), so two readers on the same team can
/// legitimately get different answers. Leaving the role out of the key would serve one to the other,
/// which is the one mistake here that is a permissions bug rather than a stale number.</para>
///
/// <para><b>The staleness window is deliberate and bounded.</b> Resolve the last finding and the bell
/// can keep it for up to <see cref="CacheDuration"/> while the page beneath already shows it gone.
/// These alerts come from a nightly sweep, so a 30-second lag is far below the resolution of the data
/// itself.</para>
/// </summary>
public class AlertFeedCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private sealed record Entry(AlertFeed Feed, DateTimeOffset CachedAtUtc);

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Null teams ("every team", a SystemAdmin) must not collapse onto empty ("no teams") — opposite answers. Sorted so team order cannot split one entry into two.</summary>
    private static string KeyFor(UserRole role, IReadOnlyList<int>? teamIds) =>
        $"{role}|{(teamIds is null ? "*" : string.Join(",", teamIds.OrderBy(id => id)))}";

    public async Task<AlertFeed> GetAsync(UserRole role, IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var key = KeyFor(role, teamIds);
        var now = timeProvider.GetUtcNow();

        if (entries.TryGetValue(key, out var hit) && now - hit.CachedAtUtc < CacheDuration)
        {
            return hit.Feed;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate: concurrent requests arrive at an expired entry together and
            // only the first should do the work.
            now = timeProvider.GetUtcNow();
            if (entries.TryGetValue(key, out hit) && now - hit.CachedAtUtc < CacheDuration)
            {
                return hit.Feed;
            }

            using var scope = scopeFactory.CreateScope();
            var feedService = scope.ServiceProvider.GetRequiredService<AlertFeedService>();
            var feed = await feedService.GetAsync(role, teamIds, cancellationToken);

            entries[key] = new Entry(feed, now);
            return feed;
        }
        finally
        {
            gate.Release();
        }
    }
}
