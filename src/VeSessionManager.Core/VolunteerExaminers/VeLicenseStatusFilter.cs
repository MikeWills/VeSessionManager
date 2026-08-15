using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// <see cref="WatchedLicenseStatusExtensions.DeriveSnapshotStatus"/> as something the database can
/// evaluate, so the VE Directory can filter by license status in SQL instead of materializing the
/// whole roster and filtering in C# (#298). That materialization is why the directory had no paging
/// path at all — you cannot <c>Skip</c>/<c>Take</c> a filter that has not been applied yet.
///
/// <para><b>The split is deliberate: classification in SQL, dates in code.</b> Everything the
/// database is good at — the status cascade, the call-sign shape test — happens there. Nothing
/// converts a timezone in SQL. "What day is it in Eastern" is resolved <i>once per request</i> by
/// <see cref="UlsSchedule.ToEasternDate"/> and passed down as three plain date constants, because
/// SQLite's <c>date('now')</c> is UTC and reaching Eastern from it means hardcoding an offset that
/// is wrong for half the year. This repo has shipped that bug twice already (see CLAUDE.md on
/// <c>UlsSchedule.ToEasternDate</c> and <c>FccUlsSchedule.EasternTimeZone</c>); it is not going to
/// ship it a third time inside a query where no C# test would see it.</para>
///
/// <para>That works because the stored side needs no conversion either: FCC dates arrive date-only
/// and are stamped at UTC midnight by <c>ExamToolsUlsLookupClient.AsUtcDate</c>, so
/// <c>LicenseExpiresUtc</c> is already a wall-clock date. Comparing it to a date constant is exactly
/// what <c>DaysUntil</c>'s <c>DayNumber - DayNumber</c> arithmetic does.</para>
///
/// <para><b>This is a second statement of a rule that already exists</b>, which is the risk the
/// codebase has been bitten by before (DUP-02, the session-completion rule). The mitigation is
/// <c>VeLicenseStatusFilterSqliteTests</c>, which asserts the SQL and C# answers agree for every
/// value of the enum over a matrix of snapshots. Change one without the other and it fails.</para>
/// </summary>
public static class VeLicenseStatusFilter
{
    /// <summary>
    /// A predicate selecting the VEs whose derived license status is <paramref name="status"/>.
    ///
    /// <para>Written as one expression that computes the status and compares it, rather than a
    /// per-status predicate, so the cascade below stays visibly in the same order as
    /// <c>DeriveSnapshotStatus</c>'s. Order is load-bearing: cancellation outranks the date tests,
    /// because a cancelled license keeps whatever expiry it had and testing dates first would report
    /// a revoked license as comfortably Active. EF renders the whole thing as one <c>CASE</c>.</para>
    /// </summary>
    public static Expression<Func<VolunteerExaminer, bool>> For(WatchedLicenseStatus status, DateTime nowUtc)
    {
        // The only timezone conversion anywhere in this file, and it happens here, in C#, once.
        var today = UlsSchedule.ToEasternDate(nowUtc);
        var lapsedCutoff = today.AddDays(-WatchedLicenseStatusExtensions.GraceDays);
        var soonCutoff = today.AddDays(WatchedLicenseStatusExtensions.RenewalWindowDays);

        return v =>
            (
                // CallSign.IsUsable, in SQL. Trimmed first, because the C# version trims before
                // inspecting and a stray space would otherwise land in the invalid-character test
                // and disagree. Needs at least one digit and one letter — that is what rules out
                // word-shaped placeholders like "UNKNOWN" that pass the character test — and nothing
                // outside letters, digits and '/'.
                //
                // GLOB rather than LIKE: LIKE has no character classes, and GLOB's are exactly the
                // shape this rule is written in. SQLite-specific, which is why the tests for this are
                // SQLite — an InMemory test could not run it at all.
                v.CallSign == null
                || !EF.Functions.Glob(v.CallSign.Trim(), "*[0-9]*")
                || !EF.Functions.Glob(v.CallSign.Trim(), "*[A-Za-z]*")
                || EF.Functions.Glob(v.CallSign.Trim(), "*[^A-Za-z0-9/]*")
                    ? WatchedLicenseStatus.NoCallSign
                : v.LicenseNotFoundAtFcc
                    ? WatchedLicenseStatus.NotFound
                : v.LicenseLastCheckedUtc == null
                    ? WatchedLicenseStatus.NotYetChecked
                : v.LicenseCancellationDateUtc != null
                    ? WatchedLicenseStatus.Cancelled
                // No expiry date at all is Active, matching DaysUntil returning null.
                : v.LicenseExpiresUtc == null
                    ? WatchedLicenseStatus.Active
                : v.LicenseExpiresUtc < lapsedCutoff
                    ? WatchedLicenseStatus.ExpiredLapsed
                : v.LicenseExpiresUtc < today
                    ? WatchedLicenseStatus.ExpiredInGrace
                : v.LicenseExpiresUtc <= soonCutoff
                    ? WatchedLicenseStatus.ExpiringSoon
                : WatchedLicenseStatus.Active
            ) == status;
    }
}
