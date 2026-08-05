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
        "PaymentReminder5Day",
        "PaymentExpirationNotice",
        "FelonyDisclosureInstructions",
        "ArrlYouthProgramInstructions"
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
