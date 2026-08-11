namespace VeSessionManager.Core.Uls;

public static class UlsSchedule
{
    /// <summary>
    /// US Eastern, the timezone every FCC-side process is anchored to — licenses are issued at
    /// 02:00 ET Tue-Sat and fee payments processed at 18:00 ET Mon-Fri. IANA id resolves
    /// cross-platform since .NET 6 (verified on both Windows and the Linux deploy target).
    ///
    /// <para>Reuse this rather than re-resolving the id. Note that a UTC-based "what day is it"
    /// check is wrong for an ET evening slot: EDT is UTC-4, so anything from ~20:00 ET onward is
    /// already tomorrow in raw UTC.</para>
    /// </summary>
    public static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>
    /// The calendar date a UTC instant falls on <b>in Eastern time</b> — the only correct left-hand
    /// side when comparing one of this app's timestamps against an FCC date.
    ///
    /// <para><b>Why this exists rather than <c>.Date</c>.</b> Every FCC date arrives date-only and is
    /// stamped at UTC midnight by <c>ExamToolsUlsLookupClient.AsUtcDate</c>, so it already *is* a
    /// wall-clock date. This app's own timestamps are real instants. Taking <c>.Date</c> on one of
    /// those answers "what day is it in London", and for any session at or after ~20:00 ET that is
    /// tomorrow — the trap the remarks above warn about, which <c>UlsWatcherService</c> then fell
    /// into anyway (issue #248): an evening session's candidates could never match an application
    /// FCC received the same evening, so they stayed <c>Unmatched</c> permanently.</para>
    ///
    /// <para><c>SpecifyKind</c> first because EF Core/SQLite returns <c>DateTimeKind.Unspecified</c>,
    /// and <c>ConvertTimeFromUtc</c> throws for a value it does not believe is UTC.</para>
    /// </summary>
    public static DateTime ToEasternDate(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), EasternTimeZone).Date;
}
