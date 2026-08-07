using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Can this VE legally serve at <i>this</i> session? (issues #107 and #142.)
///
/// <para><b>This is the question neither issue could answer alone.</b> #107 brought the license
/// state from ULS; #142 brought the accreditations. A VE needs a current license of General or
/// higher <b>and</b> accreditation with that session's VEC, and until both landed the app could only
/// ever check one half.</para>
///
/// <para><b>Session-relative, not calendar-relative.</b> The Renewal Monitor can say "expires in 12
/// days"; only this can say "expired on the day of the session you have them booked for", which is
/// the thing that actually ruins a Saturday. Every test below is against the session date, never
/// against today.</para>
///
/// <para><b>Honest about what it does not know.</b> The license half is a cached snapshot up to a
/// day old, and the accreditation half is hand-entered and verified by nobody. "No problem found" is
/// therefore never phrased as "cleared" — see <see cref="VeEligibility.Summary"/>.</para>
/// </summary>
public static class VeSessionEligibility
{
    /// <summary>A VE must hold General or higher to serve. Confirmed in issue #107.</summary>
    public const LicenseClass MinimumClass = LicenseClass.General;

    public static VeEligibility For(VolunteerExaminer volunteerExaminer, DateTime sessionStartUtc, int sessionVecId)
    {
        var problems = new List<string>();
        var unknowns = new List<string>();

        // A license is valid THROUGH its expiration date, so a session ON the expiry date is fine —
        // the same off-by-one the Renewal Monitor's day arithmetic already had to get right.
        var daysAtSession = WatchedLicenseStatusExtensions.DaysUntil(volunteerExaminer.LicenseExpiresUtc, sessionStartUtc);

        if (!CallSign.IsUsable(volunteerExaminer.CallSign))
        {
            unknowns.Add("no call sign on file, so their license cannot be checked");
        }
        else if (volunteerExaminer.LicenseLastCheckedUtc is null)
        {
            unknowns.Add("their license has not been checked yet");
        }
        else if (volunteerExaminer.LicenseNotFoundAtFcc)
        {
            problems.Add("FCC has no record of this call sign");
        }
        else if (volunteerExaminer.LicenseCancellationDateUtc is not null)
        {
            problems.Add("their license is cancelled");
        }
        else if (volunteerExaminer.LicenseExpiresUtc is null)
        {
            unknowns.Add("no expiration date is recorded");
        }
        else if (daysAtSession < 0)
        {
            problems.Add("their license expires before this session");
        }

        // Checked independently of the expiry: a current license of the wrong class is just as
        // disqualifying, and an expired Extra is two problems rather than one.
        if (volunteerExaminer.LicenseLastCheckedUtc is not null && !volunteerExaminer.LicenseNotFoundAtFcc)
        {
            if (volunteerExaminer.OperatorClass == LicenseClass.None)
            {
                unknowns.Add("their operator class is unknown");
            }
            else if (volunteerExaminer.OperatorClass < MinimumClass)
            {
                problems.Add($"they hold {volunteerExaminer.OperatorClass}, below the {MinimumClass} minimum");
            }
        }

        // Accreditation must be with THIS session's VEC. A VE accredited with one VEC cannot serve a
        // session run under another, which is invisible on a roster that only lists names.
        var accreditation = volunteerExaminer.VecAccreditations.FirstOrDefault(a => a.VecId == sessionVecId);
        if (accreditation is null)
        {
            unknowns.Add("no accreditation recorded with this session's VEC");
        }
        else if (accreditation.ExpiresUtc is { } accreditationExpiry
                 && WatchedLicenseStatusExtensions.DaysUntil(accreditationExpiry, sessionStartUtc) < 0)
        {
            problems.Add("their accreditation with this VEC expires before this session");
        }

        return new VeEligibility(problems, unknowns, volunteerExaminer.LicenseLastCheckedUtc);
    }
}

/// <summary>
/// The verdict. Three states rather than two, because "we found nothing wrong" and "we could not
/// check" are different answers and collapsing them would let an unchecked VE render as cleared.
/// </summary>
public record VeEligibility(IReadOnlyList<string> Problems, IReadOnlyList<string> Unknowns, DateTime? LicenseLastCheckedUtc)
{
    public bool HasProblem => Problems.Count > 0;
    public bool HasUnknown => Unknowns.Count > 0;

    /// <summary>Nothing to say — checked, and every test passed. The only state that renders without a marker.</summary>
    public bool IsClear => !HasProblem && !HasUnknown;

    /// <summary>
    /// Tooltip text. Always states when the license data was last refreshed, because the whole
    /// verdict rests on a cached snapshot plus hand-entered accreditation — presenting it as a live
    /// check would be the one way this feature could do harm.
    /// </summary>
    public string Summary
    {
        get
        {
            var lines = new List<string>();
            if (HasProblem) lines.Add("Cannot serve: " + string.Join("; ", Problems) + ".");
            if (HasUnknown) lines.Add("Unverified: " + string.Join("; ", Unknowns) + ".");
            if (IsClear) lines.Add("No problem found for this session's date.");

            lines.Add(LicenseLastCheckedUtc is { } checkedUtc
                ? $"License data last refreshed {checkedUtc:MMM d, yyyy}. Accreditation is hand-entered and unverified."
                : "License data has never been refreshed. Accreditation is hand-entered and unverified.");

            return string.Join(" ", lines);
        }
    }
}
