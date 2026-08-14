using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The nav badge cache (#291). The layout renders these four counts on every authenticated request,
/// so "does it actually stop re-querying" is the whole feature — and it is invisible to any test
/// that only checks the rendered number, which is correct either way.
///
/// <para>Uses a real DbContext over SQLite via <see cref="WebAppFactory"/>'s service provider, and
/// measures the cache by <b>changing the underlying data</b> and observing whether the answer moves.
/// A cache that is not caching returns the new number immediately; a cache that is returns the old
/// one until its TTL passes.</para>
/// </summary>
public class NavBadgeCountCacheTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public NavBadgeCountCacheTests(WebAppFactory factory) => _factory = factory;

    /// <summary>Advances on demand so the TTL boundary can be crossed without waiting for it.</summary>
    private sealed class MovableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private NavBadgeCountCache CreateCache(MovableTimeProvider time) =>
        new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), time);

    /// <summary>Adds an unresolved unmatched payment, which is one of the four counted badges.</summary>
    private async Task<int> AddUnresolvedPaymentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamId = (await db.Teams.AsNoTracking().FirstAsync()).Id;

        db.UnmatchedSquarePayments.Add(new UnmatchedSquarePayment
        {
            TeamId = teamId,
            SquareOrderId = $"ord-{Guid.NewGuid():N}",
            SquarePaymentId = $"sq-{Guid.NewGuid():N}",
            AmountUsd = 15m,
            ReceivedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>
    /// The property the cache exists for: a second call inside the TTL does not re-query. Proven by
    /// writing a new row between the two calls — without caching the count would have moved.
    /// </summary>
    [Fact]
    public async Task SecondCallWithinTheTtlDoesNotRequery()
    {
        var time = new MovableTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = CreateCache(time);

        var first = await cache.GetAsync(null, CancellationToken.None);

        await AddUnresolvedPaymentAsync();
        var second = await cache.GetAsync(null, CancellationToken.None);

        Assert.Equal(first.UnresolvedUnmatchedPayments, second.UnresolvedUnmatchedPayments);
    }

    /// <summary>And the other half — it is a cache, not a freeze. Past the TTL it re-reads.</summary>
    [Fact]
    public async Task AfterTheTtlItRequeries()
    {
        var time = new MovableTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = CreateCache(time);

        var first = await cache.GetAsync(null, CancellationToken.None);
        await AddUnresolvedPaymentAsync();

        time.Advance(TimeSpan.FromMinutes(5));
        var afterExpiry = await cache.GetAsync(null, CancellationToken.None);

        Assert.Equal(first.UnresolvedUnmatchedPayments + 1, afterExpiry.UnresolvedUnmatchedPayments);
    }

    /// <summary>
    /// <b>Null and empty must not share a cache entry.</b> Null means "every team" (a SystemAdmin);
    /// empty means "no teams at all". They are opposite answers, and a key function that collapsed
    /// them would serve a SystemAdmin a nav of zeros — the exact failure NavBadgeCountService's own
    /// remarks warn about, now reachable through the cache instead of the query.
    /// </summary>
    [Fact]
    public async Task NullAndEmptyTeamSetsDoNotShareAnEntry()
    {
        await AddUnresolvedPaymentAsync();

        var time = new MovableTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = CreateCache(time);

        var everyTeam = await cache.GetAsync(null, CancellationToken.None);
        var noTeams = await cache.GetAsync([], CancellationToken.None);

        Assert.True(everyTeam.UnresolvedUnmatchedPayments > 0, "A SystemAdmin (null) should see the deployment's payments.");
        Assert.Equal(0, noTeams.UnresolvedUnmatchedPayments);
    }

    /// <summary>Team order is not identity — the same set in a different order is the same question.</summary>
    [Fact]
    public async Task TeamIdOrderDoesNotCreateSeparateEntries()
    {
        var teamId = await AddUnresolvedPaymentAsync();

        var time = new MovableTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = CreateCache(time);

        var ascending = await cache.GetAsync([teamId, teamId + 500], CancellationToken.None);

        // Populated the first entry; if order made a new key, this would re-query and see the extra
        // row written in between.
        await AddUnresolvedPaymentAsync();
        var descending = await cache.GetAsync([teamId + 500, teamId], CancellationToken.None);

        Assert.Equal(ascending.UnresolvedUnmatchedPayments, descending.UnresolvedUnmatchedPayments);
    }
}
