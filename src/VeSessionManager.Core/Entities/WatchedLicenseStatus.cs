namespace VeSessionManager.Core.Entities;

/// <summary>
/// What a watched licence's row is actually telling the reader. Derived at render time from the
/// cached ULS fields rather than stored, for the same reason Session "Completed" is derived — a
/// stored copy would need rewriting every time the clock moved past a threshold, and would be wrong
/// in between.
/// </summary>
public enum WatchedLicenseStatus
{
    /// <summary>Added but never successfully looked up yet.</summary>
    NotYetChecked,

    /// <summary>Looked up, and FCC has no record of this call sign.</summary>
    NotFound,

    /// <summary>FCC cancelled or revoked the licence. Terminal — a cancelled licence is not renewable.</summary>
    Cancelled,

    /// <summary>Active, with the expiration far enough out that nobody needs to act.</summary>
    Active,

    /// <summary>A renewal application is in flight at FCC.</summary>
    RenewalPending,

    /// <summary>A renewal landed recently — the expiration date advanced.</summary>
    Renewed,

    /// <summary>Inside the renewal window and no renewal filed yet.</summary>
    ExpiringSoon,

    /// <summary>Past the expiration date but still inside the grace period — renewable without retesting, but not usable on the air.</summary>
    ExpiredInGrace,

    /// <summary>Past expiration and past the grace period. The licence is gone; re-testing is required.</summary>
    ExpiredLapsed
}

public static class WatchedLicenseStatusExtensions
{
    /// <summary>
    /// FCC opens the renewal window 90 days before a licence expires. Confirmed with the VE team
    /// 2026-08-05.
    /// </summary>
    public static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// A licence stays renewable without re-testing for two years after it expires, though it may
    /// not be operated during that time. Confirmed with the VE team 2026-08-05.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(730);

    /// <summary>
    /// How long a confirmed renewal keeps being reported as <see cref="WatchedLicenseStatus.Renewed"/>
    /// before the row settles back to <see cref="WatchedLicenseStatus.Active"/>. Purely cosmetic —
    /// long enough that someone checking a week later still sees the outcome of what they were
    /// waiting on, short enough that it doesn't become the permanent state of every row.
    /// </summary>
    public static readonly TimeSpan RenewedHighlightWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Order matters here, and each step is doing real work:
    /// <list type="number">
    /// <item>Not-checked and not-found come first — with no ULS data every date test below is
    /// meaningless, not merely false.</item>
    /// <item>Cancellation outranks everything, including a future expiration date: a cancelled
    /// record keeps whatever expiration it had, so testing dates first would report a revoked
    /// licence as comfortably Active.</item>
    /// <item>A pending renewal outranks ExpiringSoon and ExpiredInGrace — once it is filed, "expires
    /// in 12 days" is no longer the actionable fact.</item>
    /// </list>
    /// </summary>
    public static WatchedLicenseStatus DeriveStatus(this WatchedLicense licence, DateTime utcNow)
    {
        if (licence.NotFoundAtFcc) return WatchedLicenseStatus.NotFound;
        if (licence.LastCheckedUtc is null) return WatchedLicenseStatus.NotYetChecked;
        if (licence.CancellationDateUtc is not null) return WatchedLicenseStatus.Cancelled;

        if (licence.RenewalPendingSinceUtc is not null) return WatchedLicenseStatus.RenewalPending;

        if (licence.RenewalConfirmedUtc is { } confirmed && utcNow - confirmed < RenewedHighlightWindow)
        {
            return WatchedLicenseStatus.Renewed;
        }

        // No expiration date on a record that was otherwise found: nothing can be said about its
        // term, so don't invent an alarming answer.
        if (licence.ExpiredDateUtc is not { } expires) return WatchedLicenseStatus.Active;

        if (utcNow >= expires + GracePeriod) return WatchedLicenseStatus.ExpiredLapsed;
        if (utcNow >= expires) return WatchedLicenseStatus.ExpiredInGrace;
        if (expires - utcNow <= RenewalWindow) return WatchedLicenseStatus.ExpiringSoon;

        return WatchedLicenseStatus.Active;
    }

    /// <summary>Statuses a human should do something about — what the list sorts to the top and what a future digest would count.</summary>
    public static bool NeedsAttention(this WatchedLicenseStatus status) => status is
        WatchedLicenseStatus.ExpiringSoon or
        WatchedLicenseStatus.ExpiredInGrace or
        WatchedLicenseStatus.ExpiredLapsed or
        WatchedLicenseStatus.NotFound;

    /// <summary>Maps to the design system's existing chip classes — no new colours introduced.</summary>
    public static string ChipClass(this WatchedLicenseStatus status) => status switch
    {
        WatchedLicenseStatus.Active or WatchedLicenseStatus.Renewed => "chip-green",
        WatchedLicenseStatus.RenewalPending or WatchedLicenseStatus.ExpiringSoon => "chip-amber",
        WatchedLicenseStatus.ExpiredInGrace or WatchedLicenseStatus.ExpiredLapsed or WatchedLicenseStatus.Cancelled => "chip-brick",
        _ => "chip-neutral"
    };

    public static string Label(this WatchedLicenseStatus status) => status switch
    {
        WatchedLicenseStatus.NotYetChecked => "Not checked yet",
        WatchedLicenseStatus.NotFound => "Not found at FCC",
        WatchedLicenseStatus.Cancelled => "Cancelled",
        WatchedLicenseStatus.Active => "Active",
        WatchedLicenseStatus.RenewalPending => "Renewal pending",
        WatchedLicenseStatus.Renewed => "Renewed",
        WatchedLicenseStatus.ExpiringSoon => "Expiring soon",
        WatchedLicenseStatus.ExpiredInGrace => "Expired — in grace",
        WatchedLicenseStatus.ExpiredLapsed => "Lapsed",
        _ => status.ToString()
    };
}
