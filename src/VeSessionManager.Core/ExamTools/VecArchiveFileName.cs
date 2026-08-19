using System.Globalization;

namespace VeSessionManager.Core.ExamTools;

/// <summary>
/// Rebuilds the VEC archive's descriptive filename when ExamTools does not send a
/// <c>Content-Disposition</c> header (issue #197).
///
/// <para>A fallback, not the normal path — the live endpoint does send the header, verified
/// 2026-08-18. It exists because the alternative fallback, the filename in the request URL, is the
/// generic <c>ExamSession_arrl_archive.zip</c> for every session of every team: filing a run of
/// identically-named archives with ARRL would destroy the identifying value of records this team has
/// had to go back to years later.</para>
/// </summary>
public static class VecArchiveFileName
{
    /// <summary>
    /// The shape ExamTools itself produces, matched against a real 2026-04-21 ARRL receipt:
    /// <c>ExamSession_MARC_20260422_0130_arrl.zip</c>.
    ///
    /// <para><b>The timestamp is UTC</b>, taken straight from <c>Session.ScheduledStartUtc</c> — which
    /// is the opposite of the rule for the ARRL form's own <c>sessionDate</c> field, where a UTC date
    /// is wrong for most of this deployment's sessions and <c>UlsSchedule.ToEasternDate</c> is
    /// required. Both are right: this one reproduces an identifier ExamTools already minted, that one
    /// answers "what day did the exam happen".</para>
    /// </summary>
    public static string Build(string teamCode, DateTime scheduledStartUtc, string vecCode) =>
        string.Create(CultureInfo.InvariantCulture,
            $"ExamSession_{teamCode}_{scheduledStartUtc:yyyyMMdd_HHmm}_{vecCode.ToLowerInvariant()}.zip");
}
