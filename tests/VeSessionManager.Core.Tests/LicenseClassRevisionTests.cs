using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #437 — a partially-graded sitting froze the license class too low, permanently.
///
/// <para><b>Reported live on Chang Sun (HRCC, 2026-08-18):</b> ExamTools showed Elements 2, 3 and 4
/// all passed — Extra — while the app recorded <c>Unlicensed → General</c> and would never revise
/// it. ExamTools grades element by element as VEs enter results, so a poll landing after E2 and E3
/// were entered but before E4 wrote General and stopped looking.</para>
///
/// <para><b>The rule that makes the fix safe:</b> within one sitting the class can only ever go
/// <i>up</i> — a VE team never re-administers an element a candidate already holds credit for, which
/// is the premise <c>docs/exam-result-license-class.md</c> already rests on. So "never revise"
/// becomes "never revise <i>downward</i>", which keeps the protection the original guard was written
/// for — a feed must not overwrite a recorded result — while losing the bug.</para>
/// </summary>
public class LicenseClassRevisionTests
{
    /// <summary>The reported case: General recorded from a partial read, Extra once E4 lands.</summary>
    [Fact]
    public void AHigherClassFromALaterElement_ReplacesTheFrozenOne()
        => Assert.True(LicenseClassRevision.ShouldReplace(stored: LicenseClass.General, resolved: LicenseClass.Extra));

    /// <summary>
    /// The half that keeps the original guard's protection. A partial re-read, an amended paper, or a
    /// re-examination must never demote somebody already recorded higher — that would be the feed
    /// overwriting a real result, which is exactly what the <c>is null</c> guard existed to prevent
    /// and what this must not give away.
    /// </summary>
    [Theory]
    [InlineData(LicenseClass.Extra, LicenseClass.General)]
    [InlineData(LicenseClass.Extra, LicenseClass.Technician)]
    [InlineData(LicenseClass.General, LicenseClass.Technician)]
    public void ALowerClass_IsIgnored(LicenseClass stored, LicenseClass resolved)
        => Assert.False(LicenseClassRevision.ShouldReplace(stored, resolved));

    [Fact]
    public void TheSameClass_IsNotAWrite()
        => Assert.False(LicenseClassRevision.ShouldReplace(LicenseClass.General, LicenseClass.General));

    /// <summary>First result of all — the original behaviour, still the common path.</summary>
    [Theory]
    [InlineData(LicenseClass.Technician)]
    [InlineData(LicenseClass.Extra)]
    public void NothingStored_IsAlwaysWritten(LicenseClass resolved)
        => Assert.True(LicenseClassRevision.ShouldReplace(stored: null, resolved));

    /// <summary>
    /// <c>None</c> means "held nothing walking in" — a legitimate <c>InitialLicenseClass</c>, but
    /// never something earned. It must not overwrite a real class.
    /// </summary>
    [Fact]
    public void None_NeverReplacesARealClass()
        => Assert.False(LicenseClassRevision.ShouldReplace(LicenseClass.Technician, LicenseClass.None));
}
