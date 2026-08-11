using System.Reflection;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Guards the one shared definition of "PII cleared" (T02, 2026-08-03). FirstName arrived in Phase 4,
/// long after CandidatePiiFields.Clear was written, and was simply never added to it — every purged
/// candidate kept their given name indefinitely, contrary to what the Privacy page promises. Nothing
/// in the test suite could have caught that, because every test named the fields it expected to be
/// cleared and so could only ever assert what someone had already remembered.
///
/// So the central test here works the other way round: it enumerates Candidate's properties by
/// reflection and asserts that everything not on an explicit, commented allow-list of
/// deliberately-retained fields comes back null. A newly added field is therefore a failing test
/// until someone makes a decision about it.
/// </summary>
public class CandidatePiiFieldsTests
{
    private static readonly DateTime PurgedUtc = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Fields Clear deliberately does NOT null, with the reason for each. If this test fails because
    /// of a property you just added to Candidate: you added a field — decide whether it is PII and
    /// either clear it in CandidatePiiFields.Clear or add it here with a one-line reason. Do not
    /// "fix" the failure by weakening the assertion.
    /// </summary>
    private static readonly HashSet<string> DeliberatelyRetained =
    [
        // Keys and relationships — the row is kept for historical session/VE/financial stats.
        nameof(Candidate.Id),
        nameof(Candidate.SessionId),
        nameof(Candidate.Session),
        nameof(Candidate.ExamToolsApplicantId),

        // Public FCC record data, not PII (decided 2026-08-03) — keeps a purged record traceable if
        // a question about the candidate's application comes up later.
        nameof(Candidate.Frn),
        nameof(Candidate.FrnMissingAtRegistration),
        nameof(Candidate.CallSign),
        nameof(Candidate.FccUlsLicenseKey),
        nameof(Candidate.UlsApplicationFileNumber),
        nameof(Candidate.LicenseGrantDateUtc),
        nameof(Candidate.ApplicationDateEnteredUtc),
        nameof(Candidate.FccHoldReason),
        nameof(Candidate.FccPaymentStatus),

        // Records when this app last called the ULS mirror about this row — a fact about our own
        // polling, not about the person, so there is nothing in it to purge. Clearing it would also
        // be actively harmful: null sorts first in the watcher's least-recently-checked ordering, so
        // a purge would send every purged candidate back to the head of the queue (issue #247).
        nameof(Candidate.UlsLastCheckedUtc),

        // Outcome/statistics fields — what the session actually produced, with no person attached
        // once Name/FirstName/Email are gone.
        nameof(Candidate.ApplicationStatus),
        nameof(Candidate.Tested),
        nameof(Candidate.InitialLicenseClass),
        nameof(Candidate.NewLicenseClass),
        nameof(Candidate.ResultMarkedByUserId),
        nameof(Candidate.ResultMarkedByUser),
        nameof(Candidate.ResultMarkedUtc),
        nameof(Candidate.DateRegisteredUtc),

        // ...Utc tracking/idempotency stamps. Clearing these would make a scan-based job re-fire
        // (e.g. re-send a registration confirmation) against a purged row.
        nameof(Candidate.PiiPurgedUtc),
        nameof(Candidate.RegistrationConfirmationSentUtc),
        nameof(Candidate.DayBeforeReminderSentUtc),
        nameof(Candidate.UnmatchedReviewFlaggedUtc),
        nameof(Candidate.FccFeeReminderSentUtc),
        nameof(Candidate.FelonyDisclosureInstructionsSentUtc),
        nameof(Candidate.YouthProgramInstructionsSentUtc)
    ];

    /// <summary>The fields that ARE PII, stated positively so the two lists together must account for every property.</summary>
    private static readonly string[] PiiFields =
    [
        nameof(Candidate.Name),
        nameof(Candidate.FirstName),
        nameof(Candidate.Email),
        nameof(Candidate.HasFelonyDisclosure)
    ];

    private static IEnumerable<PropertyInfo> WritableProperties() =>
        typeof(Candidate)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod is { IsPublic: true });

    /// <summary>Fills every writable property with a non-null, non-default value so "still set after Clear" is distinguishable from "was never set".</summary>
    private static Candidate FullyPopulatedCandidate()
    {
        var candidate = new Candidate();

        foreach (var property in WritableProperties())
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            object? value =
                type == typeof(string) ? $"value-of-{property.Name}"
                : type == typeof(bool) ? true
                : type == typeof(int) ? 42
                : type == typeof(long) ? 42L
                : type == typeof(decimal) ? 42m
                : type == typeof(DateTime) ? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                : type == typeof(DateOnly) ? new DateOnly(2026, 1, 2)
                : type == typeof(Guid) ? Guid.NewGuid()
                : type.IsEnum ? Enum.GetValues(type).Cast<object>().Last()
                : null;

            if (value is null)
            {
                // A reference/complex type. Navigation properties are allow-listed and need no
                // value; anything else is a type this populator does not know how to fill, which
                // means the guard below would silently pass for it.
                Assert.True(DeliberatelyRetained.Contains(property.Name),
                    $"Candidate.{property.Name} is of type {property.PropertyType.Name}, which this test cannot populate. " +
                    "Decide whether it is PII: clear it in CandidatePiiFields.Clear, or add it to DeliberatelyRetained with a reason.");
                continue;
            }

            property.SetValue(candidate, value);
        }

        return candidate;
    }

    [Fact]
    public void Clear_EveryPropertyNotOnTheRetainedAllowList_IsNulled()
    {
        // Arrange
        var candidate = FullyPopulatedCandidate();

        // Act
        CandidatePiiFields.Clear(candidate, PurgedUtc);

        // Assert
        var mustBeNulled = WritableProperties().Where(p => !DeliberatelyRetained.Contains(p.Name)).ToList();

        // Non-vacuity: if the allow-list ever swallowed every property, the loop below would pass by
        // checking nothing at all.
        Assert.Equal(PiiFields.Order(), mustBeNulled.Select(p => p.Name).Order());

        foreach (var property in mustBeNulled)
        {
            Assert.True(property.GetValue(candidate) is null,
                $"Candidate.{property.Name} was still set after CandidatePiiFields.Clear. " +
                "You added a field — decide whether it is PII and either clear it in CandidatePiiFields.Clear, " +
                $"or add it to {nameof(CandidatePiiFieldsTests)}.{nameof(DeliberatelyRetained)} with a reason.");
        }
    }

    [Fact]
    public void Clear_TheTwoFieldLists_AccountForEveryCandidateProperty()
    {
        // Arrange / Act
        var unaccountedFor = WritableProperties()
            .Select(p => p.Name)
            .Where(name => !DeliberatelyRetained.Contains(name) && !PiiFields.Contains(name))
            .ToList();

        // Assert
        Assert.True(unaccountedFor.Count == 0,
            $"Candidate property/properties {string.Join(", ", unaccountedFor)} appear on neither the PII list nor the " +
            "deliberately-retained allow-list. You added a field — decide which it is.");
    }

    [Theory]
    [InlineData(nameof(Candidate.Name))]
    [InlineData(nameof(Candidate.FirstName))] // Added 2026-08-03 — the field this whole test file exists for.
    [InlineData(nameof(Candidate.Email))]
    [InlineData(nameof(Candidate.HasFelonyDisclosure))]
    public void Clear_NamedPiiField_IsNulled(string propertyName)
    {
        // Arrange
        var candidate = FullyPopulatedCandidate();

        // Act
        CandidatePiiFields.Clear(candidate, PurgedUtc);

        // Assert
        Assert.Null(typeof(Candidate).GetProperty(propertyName)!.GetValue(candidate));
    }

    [Fact]
    public void Clear_Frn_IsRetained_BecauseItIsPublicFccData()
    {
        // Decided 2026-08-03: an FRN is public FCC data, not PII, and retaining it — like CallSign
        // and the ULS keys — keeps the record traceable after the purge.
        var candidate = new Candidate { Name = "Roana Glory", FirstName = "Roana", Frn = "0012345678" };

        CandidatePiiFields.Clear(candidate, PurgedUtc);

        Assert.Equal("0012345678", candidate.Frn);
    }

    [Fact]
    public void Clear_StampsPiiPurgedUtcWithTheSuppliedTime()
    {
        var candidate = new Candidate { Name = "Roana Glory" };

        CandidatePiiFields.Clear(candidate, PurgedUtc);

        Assert.Equal(PurgedUtc, candidate.PiiPurgedUtc);
    }

    [Fact]
    public void Clear_AlsoClearsEveryLoadedPaymentsLiveSquareLink()
    {
        var candidate = new Candidate { Name = "Roana Glory" };
        candidate.Payments.Add(new Payment
        {
            Reason = PaymentReason.InitialExam,
            Amount = 15m,
            Status = PaymentStatus.Paid,
            PaymentLinkUrl = "https://square.link/u/abc",
            SquarePaymentReferenceId = "sq-order-1"
        });

        CandidatePiiFields.Clear(candidate, PurgedUtc);

        var payment = Assert.Single(candidate.Payments);
        Assert.Null(payment.PaymentLinkUrl);
        Assert.Null(payment.SquarePaymentReferenceId);
        Assert.Equal(15m, payment.Amount); // non-PII financial history is untouched
    }

    [Fact]
    public void Clear_RunTwice_IsIdempotent_AndDoesNotThrow()
    {
        // The repair pass in PiiPurgeService re-runs Clear over already-purged rows, so this has to hold.
        var candidate = FullyPopulatedCandidate();

        CandidatePiiFields.Clear(candidate, PurgedUtc);
        CandidatePiiFields.Clear(candidate, PurgedUtc);

        Assert.Null(candidate.Name);
        Assert.Null(candidate.FirstName);
    }
}
