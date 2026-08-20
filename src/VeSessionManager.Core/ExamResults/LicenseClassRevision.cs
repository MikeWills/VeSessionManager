using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.ExamResults;

/// <summary>
/// When a already-recorded license class may be replaced by a newly resolved one (issue #437).
///
/// <para><b>What went wrong.</b> The class used to be written under <c>if (NewLicenseClass is null)</c>
/// — once, from whatever elements happened to be graded at the moment of the first poll that saw any
/// graded element, and never revised. ExamTools grades element by element as VEs enter results, so a
/// poll landing after Elements 2 and 3 were entered but before Element 4 recorded <b>General</b> for
/// somebody who had earned <b>Extra</b>. Reported live on Chang Sun (HRCC, 2026-08-18).</para>
///
/// <para><b>Why "never revise" was nearly right.</b> The instinct behind it is correct and worth
/// keeping: a feed must not overwrite a result the app already recorded. It was simply too blunt in
/// one direction. <b>Within one sitting the class can only ever go up</b> — a VE team never
/// re-administers an element a candidate already holds credit for, which is the same premise
/// <c>docs/exam-result-license-class.md</c> rests on for deriving the class from elements at all. So
/// the rule becomes <i>revise upward, never downward</i>: a partial re-read, an amended paper, or a
/// re-examination can never demote anybody, and a later element can still land.</para>
///
/// <para><b>Why this is its own type.</b> The comparison is an ordering question about an enum whose
/// numeric order is an implementation detail of <c>LicenseClass</c>, and getting it backwards silently
/// demotes real people. It is worth naming, and worth testing directly rather than only through the
/// service that calls it.</para>
/// </summary>
public static class LicenseClassRevision
{
    /// <summary>
    /// Whether <paramref name="resolved"/> should replace <paramref name="stored"/>.
    ///
    /// <para><c>None</c> is never written over a real class: it means "held nothing walking in",
    /// which is a legitimate <c>InitialLicenseClass</c> but never something earned.</para>
    /// </summary>
    public static bool ShouldReplace(LicenseClass? stored, LicenseClass resolved)
    {
        if (resolved == LicenseClass.None)
        {
            return false;
        }

        return stored is not { } current || Rank(resolved) > Rank(current);
    }

    /// <summary>
    /// Explicit rather than a cast on the enum. <c>LicenseClass</c>'s declaration order happens to be
    /// ascending today; a future member added in the wrong place would silently invert this
    /// comparison, and the failure would be a candidate quietly demoted rather than an error.
    /// </summary>
    private static int Rank(LicenseClass licenseClass) => licenseClass switch
    {
        LicenseClass.Technician => 1,
        LicenseClass.General => 2,
        LicenseClass.Extra => 3,
        _ => 0
    };
}
