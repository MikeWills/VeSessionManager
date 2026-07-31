namespace VeSessionManager.Core.Uls;

public static class UlsSchedule
{
    /// <summary>
    /// US Eastern, the timezone every FCC-side process is anchored to — licences are issued at
    /// 02:00 ET Tue-Sat and fee payments processed at 18:00 ET Mon-Fri. IANA id resolves
    /// cross-platform since .NET 6 (verified on both Windows and the Linux deploy target).
    ///
    /// <para>Reuse this rather than re-resolving the id. Note that a UTC-based "what day is it"
    /// check is wrong for an ET evening slot: EDT is UTC-4, so anything from ~20:00 ET onward is
    /// already tomorrow in raw UTC.</para>
    /// </summary>
    public static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
}
