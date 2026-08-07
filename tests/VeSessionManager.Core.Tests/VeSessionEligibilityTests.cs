using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// "Can this VE legally serve at THIS session?" — the question issues #107 and #142 could only
/// answer together, and the one the Renewal Monitor structurally cannot ask because it has no
/// concept of a session.
/// </summary>
public class VeSessionEligibilityTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
    private const int ArrlVecId = 1;

    private static VolunteerExaminer Ve(
        DateTime? expires,
        LicenseClass operatorClass = LicenseClass.Extra,
        string? callSign = "N2SPG",
        bool everChecked = true,
        bool notFound = false,
        DateTime? cancelled = null,
        int? accreditedWithVecId = ArrlVecId,
        DateTime? accreditationExpires = null)
    {
        var person = new VolunteerExaminer
        {
            Name = "Sam Granger",
            CallSign = callSign,
            CreatedUtc = Now.AddYears(-1),
            // A nullable "lastChecked" defaulting to Now made "never checked" impossible to express —
            // the flag says it outright instead.
            LicenseLastCheckedUtc = everChecked ? Now : null,
            LicenseNotFoundAtFcc = notFound,
            LicenseCancellationDateUtc = cancelled,
            LicenseExpiresUtc = expires,
            OperatorClass = operatorClass
        };

        if (accreditedWithVecId is { } vecId)
        {
            person.VecAccreditations.Add(new VeVecAccreditation
            {
                VolunteerExaminerId = person.Id,
                VecId = vecId,
                ExpiresUtc = accreditationExpires,
                CreatedUtc = Now
            });
        }

        return person;
    }

    [Fact]
    public void CurrentExtraAccreditedWithTheSessionsVec_IsClear()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddYears(4)), Now.AddDays(14), ArrlVecId);

        Assert.True(result.IsClear);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Unknowns);
    }

    /// <summary>
    /// The whole point of the feature. Today the license is perfectly current; on the day they are
    /// booked for, it is not. A calendar-relative check would call this fine.
    /// </summary>
    [Fact]
    public void LicenseValidTodayButExpiredBySessionDate_IsAProblem()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddDays(10)), Now.AddDays(30), ArrlVecId);

        Assert.True(result.HasProblem);
        Assert.Contains(result.Problems, p => p.Contains("expires before this session"));
    }

    /// <summary>A license is valid THROUGH its expiration date, so a session on that very day is fine — the same off-by-one the Renewal Monitor's day arithmetic already had to get right.</summary>
    [Fact]
    public void SessionOnTheExpiryDateItself_IsStillFine()
    {
        var expiry = Now.AddDays(30).Date;
        var result = VeSessionEligibility.For(Ve(expiry), expiry, ArrlVecId);

        Assert.False(result.HasProblem);
    }

    [Fact]
    public void TechnicianIsBelowTheMinimumClass()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddYears(4), LicenseClass.Technician), Now.AddDays(14), ArrlVecId);

        Assert.True(result.HasProblem);
        Assert.Contains(result.Problems, p => p.Contains("below the General minimum"));
    }

    [Fact]
    public void GeneralIsEnough()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddYears(4), LicenseClass.General), Now.AddDays(14), ArrlVecId);

        Assert.True(result.IsClear);
    }

    /// <summary>An expired Technician is two problems, not one — the class check is independent of the expiry check on purpose.</summary>
    [Fact]
    public void ExpiredAndUnderclassed_ReportsBoth()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddDays(-1), LicenseClass.Technician), Now.AddDays(14), ArrlVecId);

        Assert.Equal(2, result.Problems.Count);
    }

    /// <summary>Accreditation is with a VEC, not in general — serving a session run under another VEC is exactly the gap a roster of names hides.</summary>
    [Fact]
    public void AccreditedWithADifferentVec_IsUnverifiedForThisSession()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddYears(4), accreditedWithVecId: 99), Now.AddDays(14), ArrlVecId);

        Assert.True(result.HasUnknown);
        Assert.Contains(result.Unknowns, u => u.Contains("no accreditation recorded with this session's VEC"));
    }

    [Fact]
    public void AccreditationExpiringBeforeTheSession_IsAProblem()
    {
        var result = VeSessionEligibility.For(
            Ve(Now.AddYears(4), accreditationExpires: Now.AddDays(5)), Now.AddDays(30), ArrlVecId);

        Assert.Contains(result.Problems, p => p.Contains("accreditation with this VEC expires before this session"));
    }

    /// <summary>No recorded expiry is not an expired accreditation — some VECs simply do not re-accredit on a cycle.</summary>
    [Fact]
    public void AccreditationWithNoExpiry_IsNotTreatedAsExpired()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddYears(4), accreditationExpires: null), Now.AddDays(30), ArrlVecId);

        Assert.True(result.IsClear);
    }

    /// <summary>
    /// "We could not check" must never collapse into "nothing wrong". A VE with no usable call sign
    /// is unverified, not cleared — the distinction is the reason the result has three states.
    /// </summary>
    [Fact]
    public void VeWithNoUsableCallSign_IsUnverifiedNotClear()
    {
        var result = VeSessionEligibility.For(Ve(null, callSign: "<UNKNOWN>", everChecked: false), Now.AddDays(14), ArrlVecId);

        Assert.False(result.IsClear);
        Assert.False(result.HasProblem);
        Assert.True(result.HasUnknown);
        Assert.Contains(result.Unknowns, u => u.Contains("no call sign on file"));
    }

    [Fact]
    public void NeverCheckedLicense_IsUnverified()
    {
        var result = VeSessionEligibility.For(Ve(null, everChecked: false), Now.AddDays(14), ArrlVecId);

        Assert.True(result.HasUnknown);
        Assert.Contains(result.Unknowns, u => u.Contains("has not been checked yet"));
    }

    [Fact]
    public void CancelledLicense_IsAProblem()
    {
        var result = VeSessionEligibility.For(Ve(Now.AddYears(4), cancelled: Now.AddDays(-5)), Now.AddDays(14), ArrlVecId);

        Assert.Contains(result.Problems, p => p.Contains("cancelled"));
    }

    [Fact]
    public void NotFoundAtFcc_IsAProblem()
    {
        var result = VeSessionEligibility.For(Ve(null, notFound: true), Now.AddDays(14), ArrlVecId);

        Assert.Contains(result.Problems, p => p.Contains("no record of this call sign"));
    }

    /// <summary>
    /// The verdict rests on a snapshot up to a day old plus hand-entered accreditation. It must say
    /// so every time, or the marker will be read as a live check the first time someone relies on it.
    /// </summary>
    [Fact]
    public void SummaryAlwaysStatesHowStaleTheDataIs()
    {
        Assert.Contains("last refreshed", VeSessionEligibility.For(Ve(Now.AddYears(4)), Now.AddDays(14), ArrlVecId).Summary);
        Assert.Contains("never been refreshed",
            VeSessionEligibility.For(Ve(null, everChecked: false), Now.AddDays(14), ArrlVecId).Summary);
        Assert.Contains("hand-entered and unverified",
            VeSessionEligibility.For(Ve(Now.AddYears(4)), Now.AddDays(14), ArrlVecId).Summary);
    }
}
