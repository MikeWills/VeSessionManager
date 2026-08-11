using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Every persisted enum's integer values, written out literally.
///
/// <para><b>Why this looks like it is testing the language.</b> It is not — it is testing the
/// database. These values are what is stored in SQLite, so inserting a member mid-list renumbers
/// every existing row: a <c>Granted</c> candidate silently becomes <c>Failed</c>, a
/// <c>SessionManager</c> becomes a <c>TeamAdmin</c>. Nothing about that fails to compile, no
/// migration is generated, and no screen looks wrong until someone notices the data is nonsense.</para>
///
/// <para>Pinning the values (2026-08-11, audit T21) made that impossible to do by accident. This
/// test is what makes the pins load-bearing rather than decorative: renumbering to "tidy up" fails
/// here, with a message saying why.</para>
///
/// <para><b>Adding a member is fine</b> — append with the next free number and add it below. Changing
/// an existing one is not, and no amount of "but it's only used internally" makes it safe: the rows
/// were written under the old number and nothing rewrites them.</para>
/// </summary>
public class PersistedEnumValueTests
{
    [Fact]
    public void SessionStatusValuesAreStable()
    {
        Assert.Equal(0, (int)SessionStatus.Active);
        Assert.Equal(1, (int)SessionStatus.Cancelled);
    }

    [Fact]
    public void VecSubmissionStatusValuesAreStable()
    {
        Assert.Equal(0, (int)VecSubmissionStatus.NotSubmitted);
        Assert.Equal(1, (int)VecSubmissionStatus.Submitted);
    }

    /// <summary>
    /// The one where a silent shift would be worst: these drive whether a candidate is chased, left
    /// alone, or reported to the VEC as passed.
    /// </summary>
    [Fact]
    public void CandidateApplicationStatusValuesAreStable()
    {
        Assert.Equal(0, (int)CandidateApplicationStatus.Unmatched);
        Assert.Equal(1, (int)CandidateApplicationStatus.Received);
        Assert.Equal(2, (int)CandidateApplicationStatus.Granted);
        Assert.Equal(3, (int)CandidateApplicationStatus.Failed);
        Assert.Equal(4, (int)CandidateApplicationStatus.NotTested);
    }

    [Fact]
    public void LicenseClassValuesAreStable()
    {
        Assert.Equal(0, (int)LicenseClass.None);
        Assert.Equal(1, (int)LicenseClass.Technician);
        Assert.Equal(2, (int)LicenseClass.General);
        Assert.Equal(3, (int)LicenseClass.Extra);
    }

    [Fact]
    public void PaymentReasonValuesAreStable()
    {
        Assert.Equal(0, (int)PaymentReason.InitialExam);
        Assert.Equal(1, (int)PaymentReason.Retest);
    }

    /// <summary>
    /// <c>InitialExam = 0</c> is doubly load-bearing: the filtered unique index on
    /// <c>Payments (CandidateId, Reason)</c> is built from this value, so a shift would silently
    /// change which rows the index constrains — and that index is what stops duplicate payment links.
    /// </summary>
    [Fact]
    public void PaymentReasonInitialExamIsTheValueTheUniqueIndexFiltersOn()
    {
        Assert.Equal(0, (int)PaymentReason.InitialExam);
    }

    [Fact]
    public void FccApplicationHoldReasonValuesAreStable()
    {
        Assert.Equal(0, (int)FccApplicationHoldReason.None);
        Assert.Equal(1, (int)FccApplicationHoldReason.RedLight);
        Assert.Equal(2, (int)FccApplicationHoldReason.BasicQualification);
        Assert.Equal(3, (int)FccApplicationHoldReason.RedLightAndBasicQualification);
    }

    [Fact]
    public void FccApplicationPaymentStatusValuesAreStable()
    {
        Assert.Equal(0, (int)FccApplicationPaymentStatus.Unknown);
        Assert.Equal(1, (int)FccApplicationPaymentStatus.PendingVerification);
        Assert.Equal(2, (int)FccApplicationPaymentStatus.Paid);
    }

    [Fact]
    public void PaymentStatusValuesAreStable()
    {
        Assert.Equal(0, (int)PaymentStatus.Unpaid);
        Assert.Equal(1, (int)PaymentStatus.Paid);
        Assert.Equal(2, (int)PaymentStatus.NotApplicable);
    }

    /// <summary>A shift here is an authorization change: every stored role would mean something else.</summary>
    [Fact]
    public void UserRoleValuesAreStable()
    {
        Assert.Equal(0, (int)UserRole.SystemAdmin);
        Assert.Equal(1, (int)UserRole.TeamAdmin);
        Assert.Equal(2, (int)UserRole.SessionManager);
        Assert.Equal(3, (int)UserRole.TeamLead);
    }

    [Fact]
    public void HistoricalImportStatusValuesAreStable()
    {
        Assert.Equal(0, (int)HistoricalImportStatus.Pending);
        Assert.Equal(1, (int)HistoricalImportStatus.Running);
        Assert.Equal(2, (int)HistoricalImportStatus.Completed);
        Assert.Equal(3, (int)HistoricalImportStatus.Failed);
    }

    /// <summary>
    /// Catches a member added without being pinned. A new member picks up an implicit ordinal, which
    /// is safe when appended and a data-corruption bug when inserted — this fails either way, so the
    /// person adding it has to come here, look at the numbers, and confirm they appended.
    /// </summary>
    [Theory]
    [InlineData(typeof(SessionStatus), 2)]
    [InlineData(typeof(VecSubmissionStatus), 2)]
    [InlineData(typeof(CandidateApplicationStatus), 5)]
    [InlineData(typeof(LicenseClass), 4)]
    [InlineData(typeof(PaymentReason), 2)]
    [InlineData(typeof(FccApplicationHoldReason), 4)]
    [InlineData(typeof(FccApplicationPaymentStatus), 3)]
    [InlineData(typeof(PaymentStatus), 3)]
    [InlineData(typeof(UserRole), 4)]
    [InlineData(typeof(HistoricalImportStatus), 4)]
    public void MemberCountIsUnchanged(Type enumType, int expected)
    {
        Assert.Equal(expected, Enum.GetValues(enumType).Length);
    }
}
