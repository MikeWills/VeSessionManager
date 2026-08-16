using VeSessionManager.Core.Email;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Guards that every seeded template Key has a trigger description on the admin page.
///
/// <para><b>What this cannot do:</b> tell whether the prose is still <i>true</i>. If a trigger
/// condition changes in CandidateNotificationService/PaymentReminderService/SessionActionService,
/// the text in EmailTemplateTriggers has to be updated by hand — nothing here will catch a
/// description that has quietly become wrong.</para>
/// </summary>
public class EmailTemplateTriggersTests
{
    // Same list EmailTemplatePlaceholdersTests keeps in sync with EmailDefaultsSeeder.
    private static readonly string[] SeededKeys =
    [
        "RegistrationConfirmation",
        "DayBeforeReminder",
        "FccFeeReminder5Day",
        "PaymentExpirationNotice",
        "FelonyDisclosureInstructions",
        "ArrlYouthProgramInstructions",
        "GettingStartedLocally"
    ];

    public static IEnumerable<object[]> SeededKeyData() => SeededKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(SeededKeyData))]
    public void EverySeededKey_HasATrigger(string key)
    {
        var trigger = EmailTemplateTriggers.For(key);

        Assert.NotNull(trigger);
        Assert.False(string.IsNullOrWhiteSpace(trigger!.Cadence));
        Assert.False(string.IsNullOrWhiteSpace(trigger.Recipient));
        Assert.False(string.IsNullOrWhiteSpace(trigger.Description));
    }

    /// <summary>
    /// Every template belongs to exactly one phase, and the enum's declaration order is the display
    /// order the page relies on.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeededKeyData))]
    public void EverySeededKey_HasAPhase(string key)
    {
        Assert.Contains(EmailTemplateTriggers.For(key)!.Phase, Enum.GetValues<EmailTemplatePhase>());
    }

    [Fact]
    public void PhasesReadInTheOrderThingsActuallyHappen()
    {
        Assert.True((int)EmailTemplatePhase.AtRegistration < (int)EmailTemplatePhase.PreSession);
        Assert.True((int)EmailTemplatePhase.PreSession < (int)EmailTemplatePhase.PostSession);
    }

    /// <summary>
    /// Anything gated on the FCC entering an application is necessarily post-session, whatever the
    /// template name suggests. Only two are now: FelonyDisclosureInstructions left this list in #221
    /// when it stopped riding along with marking a session completed.
    /// </summary>
    [Theory]
    [InlineData("FccFeeReminder5Day")]
    [InlineData("PaymentExpirationNotice")]
    // FelonyDisclosureInstructions was here until #221. It is PreSession now, and deliberately so:
    // it used to ride along with marking a session completed, which meant it could only ever arrive
    // after the exam — when the candidate can no longer easily ask anyone about it.
    public void FccGatedAndSessionCompletionEmails_ArePostSession(string key)
    {
        Assert.Equal(EmailTemplatePhase.PostSession, EmailTemplateTriggers.For(key)!.Phase);
    }

    /// <summary>
    /// A retired key must not also be live. The two lists are hand-maintained, and a key in both
    /// would make the admin page say a template is dead while something still sends it — the exact
    /// wrong direction to be wrong in.
    /// </summary>
    [Fact]
    public void NoKeyIsBothRetiredAndLive()
    {
        Assert.DoesNotContain(EmailTemplateTriggers.Retired, EmailTemplateTriggers.ByKey.ContainsKey);
    }

    /// <summary>
    /// And a retired key must no longer be seeded, or every new deployment would create a row the
    /// page immediately labels as never sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeededKeyData))]
    public void NoSeededKeyIsRetired(string key)
    {
        Assert.False(EmailTemplateTriggers.IsRetired(key), $"'{key}' is still seeded but marked retired.");
    }

    [Fact]
    public void UnknownKey_ReturnsNull_SoThePageShowsNothingRatherThanInventingOne()
    {
        Assert.Null(EmailTemplateTriggers.For("SomeUnknownKey"));
    }

    [Fact]
    public void Cadence_IsOneOfTheTwoThePageStyles()
    {
        // The page picks a chip colour from this value, so a third spelling would render as the
        // "on demand" style without anyone noticing.
        Assert.All(EmailTemplateTriggers.ByKey.Values,
            t => Assert.Contains(t.Cadence, new[] { "Automatic", "On demand" }));
    }

    /// <summary>
    /// The one genuinely surprising entry, pinned because it is the most consequential thing an
    /// admin could get wrong: editing this template as though a candidate will read it.
    /// </summary>
    [Fact]
    public void PaymentExpirationNotice_IsMarkedAsGoingToTheTeam_NotTheCandidate()
    {
        var trigger = EmailTemplateTriggers.For("PaymentExpirationNotice");

        Assert.Contains("not the candidate", trigger!.Recipient, StringComparison.OrdinalIgnoreCase);
    }
}
