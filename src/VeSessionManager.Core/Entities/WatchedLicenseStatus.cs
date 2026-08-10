using VeSessionManager.Core.Uls;
using CallSign = VeSessionManager.Core.CallSign;
namespace VeSessionManager.Core.Entities;

/// <summary>
/// What a watched license's row is actually telling the reader. Derived at render time from the
/// cached ULS fields rather than stored, for the same reason Session "Completed" is derived — a
/// stored copy would need rewriting every time the clock moved past a threshold, and would be wrong
/// in between.
/// </summary>
public enum WatchedLicenseStatus
{
    /// <summary>Added but never successfully looked up yet.</summary>
    NotYetChecked,

    /// <summary>
    /// There is no call sign to look up, so nothing can be said about this license — issue #107's
    /// open question 3. Unreachable for a <see cref="WatchedLicense"/>, whose call sign is required;
    /// it exists for a <see cref="VolunteerExaminer"/>, whose call sign ExamTools may simply not
    /// report (or may report as the literal "&lt;UNKNOWN&gt;"). A state of its own because "we
    /// cannot check this" must never render as healthy.
    /// </summary>
    NoCallSign,

    /// <summary>Looked up, and FCC has no record of this call sign.</summary>
    NotFound,

    /// <summary>FCC cancelled or revoked the license. Terminal — a cancelled license is not renewable.</summary>
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

    /// <summary>Past expiration and past the grace period. The license is gone; re-testing is required.</summary>
    ExpiredLapsed
}

public static class WatchedLicenseStatusExtensions
{
    /// <summary>
    /// FCC opens the renewal window 90 days before a license expires. Confirmed with the VE team
    /// 2026-08-05.
    /// </summary>
    public const int RenewalWindowDays = 90;

    /// <summary>
    /// A license stays renewable without re-testing for two years after it expires, though it may
    /// not be operated during that time. Confirmed with the VE team 2026-08-05.
    /// </summary>
    public const int GraceDays = 730;

    /// <summary>
    /// How long a confirmed renewal keeps being reported as <see cref="WatchedLicenseStatus.Renewed"/>
    /// before the row settles back to <see cref="WatchedLicenseStatus.Active"/>. Purely cosmetic —
    /// long enough that someone checking a week later still sees the outcome of what they were
    /// waiting on, short enough that it doesn't become the permanent state of every row.
    /// </summary>
    public static readonly TimeSpan RenewedHighlightWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// The record-only half of the rules, shared by everything holding an
    /// <see cref="ILicenseSnapshot"/> — <see cref="WatchedLicense"/> and, since issue #107,
    /// <see cref="VolunteerExaminer"/>.
    ///
    /// <para>Knows nothing about renewals: that lifecycle is the Renewal Monitor's alone, and
    /// <see cref="DeriveStatus(WatchedLicense, DateTime)"/> layers it on top of this. Sharing the
    /// thresholds is the entire point — two copies of "90 days" and "two years" would have drifted
    /// the first time either was tuned.</para>
    /// </summary>
    public static WatchedLicenseStatus DeriveSnapshotStatus(this ILicenseSnapshot snapshot, DateTime utcNow)
    {
        // Before "have we checked?": with no usable call sign there is nothing to check, and
        // NotYetChecked would imply the sweep is merely behind and will get to it. Uses the same
        // CallSign.IsUsable as the sync, so ExamTools' literal "<UNKNOWN>" lands here rather than
        // being looked up forever and reported as not found.
        if (!CallSign.IsUsable(snapshot.CallSign)) return WatchedLicenseStatus.NoCallSign;

        if (snapshot.LicenseNotFoundAtFcc) return WatchedLicenseStatus.NotFound;
        if (snapshot.LicenseLastCheckedUtc is null) return WatchedLicenseStatus.NotYetChecked;
        if (snapshot.LicenseCancellationDateUtc is not null) return WatchedLicenseStatus.Cancelled;

        if (DaysUntil(snapshot.LicenseExpiresUtc, utcNow) is not { } days) return WatchedLicenseStatus.Active;

        if (days < -GraceDays) return WatchedLicenseStatus.ExpiredLapsed;
        if (days < 0) return WatchedLicenseStatus.ExpiredInGrace;
        if (days <= RenewalWindowDays) return WatchedLicenseStatus.ExpiringSoon;

        return WatchedLicenseStatus.Active;
    }

    /// <summary>
    /// Whole calendar days from "today in US Eastern" to a date. Extracted from
    /// <see cref="DaysUntilExpiry"/> so the VE sweep and the Renewal Monitor cannot come to
    /// different conclusions about what day it is.
    /// </summary>
    public static int? DaysUntil(DateTime? date, DateTime utcNow)
    {
        if (date is not { } target) return null;

        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), UlsSchedule.EasternTimeZone));

        return DateOnly.FromDateTime(target).DayNumber - today.DayNumber;
    }

    /// <summary>
    /// The Renewal Monitor's full rules: the shared record status above, plus the renewal lifecycle
    /// that only a watched license has.
    ///
    /// <para>Order matters and each step is doing real work:</para>
    /// <list type="number">
    /// <item>Not-checked and not-found come first — with no ULS data every date test below is
    /// meaningless, not merely false.</item>
    /// <item>Cancellation outranks everything, including a future expiration date: a cancelled
    /// record keeps whatever expiration it had, so testing dates first would report a revoked
    /// license as comfortably Active.</item>
    /// <item>A pending renewal outranks ExpiringSoon and ExpiredInGrace — once it is filed, "expires
    /// in 12 days" is no longer the actionable fact.</item>
    /// </list>
    ///
    /// <para>Deliberately still its own method rather than a call into
    /// <see cref="DeriveSnapshotStatus"/>: the renewal checks sit *between* cancellation and the
    /// date tests, so delegating would need the shared rule to know about a lifecycle that means
    /// nothing to a VE. The thresholds are what had to be shared, and they are.</para>
    /// </summary>
    public static WatchedLicenseStatus DeriveStatus(this WatchedLicense license, DateTime utcNow)
    {
        if (license.NotFoundAtFcc) return WatchedLicenseStatus.NotFound;
        if (license.LastCheckedUtc is null) return WatchedLicenseStatus.NotYetChecked;
        if (license.CancellationDateUtc is not null) return WatchedLicenseStatus.Cancelled;

        // Renewed outranks RenewalPending, not the other way round: a renewal issued within the last
        // month cannot have been followed by a real new one (they are ten years apart), so a pending
        // flag alongside a recent confirmation is FCC's granted application lingering in its pending
        // list — or this app having been re-armed by it. A license that has been issued must never
        // walk backwards to "pending" on the screen, whatever the stored fields say.
        if (license.RenewalConfirmedUtc is { } confirmed && utcNow - confirmed < RenewedHighlightWindow)
        {
            return WatchedLicenseStatus.Renewed;
        }

        if (license.RenewalPendingSinceUtc is not null) return WatchedLicenseStatus.RenewalPending;

        // No expiration date on a record that was otherwise found: nothing can be said about its
        // term, so don't invent an alarming answer.
        if (license.DaysUntilExpiry(utcNow) is not { } days) return WatchedLicenseStatus.Active;

        // Strictly less-than, both times: a license is valid THROUGH its expiration date, so days == 0
        // ("expires today") is still current, and the grace period likewise runs through its final
        // day. Comparing the raw instants instead — as this did originally — flipped a license to
        // Expired at midnight on its expiry date, a full day early.
        if (days < -GraceDays) return WatchedLicenseStatus.ExpiredLapsed;
        if (days < 0) return WatchedLicenseStatus.ExpiredInGrace;
        if (days <= RenewalWindowDays) return WatchedLicenseStatus.ExpiringSoon;

        return WatchedLicenseStatus.Active;
    }

    /// <summary>
    /// Whole calendar days until the license expires — negative once it has. The single definition,
    /// used by both <see cref="DeriveStatus"/> and the Renewal Monitor's day pill, so the number a
    /// human reads and the status they see can never disagree.
    ///
    /// <para><b>Calendar days, not elapsed hours.</b> An FCC expiration date carries no time of day;
    /// it is stored as midnight UTC. Subtracting instants and flooring — the original implementation
    /// — measures the time to the *start* of that date, so on the 5th a license expiring on the 7th
    /// read as "1 d" rather than 2. Nobody counts days that way.</para>
    ///
    /// <para><b>"Today" is Eastern, not UTC.</b> Anything from ~8pm ET onward is already tomorrow in
    /// raw UTC, so a UTC-based "what day is it" would silently drop a day every evening. Same rule
    /// the rest of this codebase follows for day arithmetic (see CLAUDE.md).</para>
    /// </summary>
    public static int? DaysUntilExpiry(this WatchedLicense license, DateTime utcNow)
    {
        return DaysUntil(license.ExpiredDateUtc, utcNow);
    }

    /// <summary>Statuses a human should do something about — what the list sorts to the top and what a future digest would count.</summary>
    public static bool NeedsAttention(this WatchedLicenseStatus status) => status is
        WatchedLicenseStatus.ExpiringSoon or
        WatchedLicenseStatus.ExpiredInGrace or
        WatchedLicenseStatus.ExpiredLapsed or
        WatchedLicenseStatus.NotFound or
        WatchedLicenseStatus.NoCallSign;

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
        WatchedLicenseStatus.NoCallSign => "No call sign on file",
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
