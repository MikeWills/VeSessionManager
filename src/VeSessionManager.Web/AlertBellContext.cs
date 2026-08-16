using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// What _AlertBell.cshtml needs to ask <see cref="AlertFeedCache"/> for a feed. Both halves are
/// already resolved by _AppLayout.cshtml on every request, so they are handed down rather than
/// re-derived — the layout paid for that lesson once, when it issued a bare user load and then a
/// second fully-included one on top of it (see CurrentUserLoader.GetCachedUserWithManagerAsync).
/// </summary>
/// <param name="Role">Null for a request with no signed-in user; the partial then renders nothing.</param>
/// <param name="TeamIds">Null means "every team" (SystemAdmin), an empty list means none — the convention throughout.</param>
public record AlertBellContext(UserRole? Role, IReadOnlyList<int>? TeamIds);
