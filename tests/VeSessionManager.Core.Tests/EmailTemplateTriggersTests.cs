using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Guards that every seeded template is accounted for on the admin page — either by a rule that sends
/// it, or by a hand-written description of the button that does.
///
/// <para><b>The registry shrank in #401 PR2.</b> It used to describe all seven templates, including
/// the four sent automatically, with their conditions in prose: "within the next 24 hours", "5 days".
/// Those are per-team rules now, so that prose was describing one deployment's defaults as the app's
/// behaviour. What the page shows for an automatic template is read from the rules; what is left here
/// is only the on-demand ones, which no rule can describe because a person decides.</para>
///
/// <para><b>What this still cannot do:</b> tell whether the remaining prose is <i>true</i>. If a
/// button's condition changes, the text has to change with it and nothing here will notice.</para>
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

    /// <summary>The template each seeded rule points at — the other half of "what sends this".</summary>
    private static readonly string[] KeysASeededRuleSends =
    [
        "RegistrationConfirmation",
        "DayBeforeReminder",
        "FccFeeReminder5Day",
        "PaymentExpirationNotice"
    ];

    public static IEnumerable<object[]> SeededKeyData() => SeededKeys.Select(k => new object[] { k });

    /// <summary>
    /// Every seeded template is explained by exactly one of the two mechanisms. Stated as an
    /// exclusive-or rather than two separate assertions because the failure worth catching is a
    /// template that falls through both — the page would then show it with no indication of what
    /// sends it, which is the state the registry existed to prevent.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeededKeyData))]
    public void EverySeededKey_IsEitherSentByASeededRule_OrDescribedAsOnDemand(string key)
    {
        var sentByRule = KeysASeededRuleSends.Contains(key);
        var describedByHand = EmailTemplateTriggers.For(key) is not null;

        Assert.True(sentByRule ^ describedByHand,
            $"'{key}' is {(sentByRule && describedByHand ? "described in both places" : "explained by neither")}.");
    }

    [Theory]
    [MemberData(nameof(SeededKeyData))]
    public void AnOnDemandDescription_SaysWhoGetsItAndWhatCausesIt(string key)
    {
        if (EmailTemplateTriggers.For(key) is not { } trigger)
        {
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(trigger.Recipient));
        Assert.False(string.IsNullOrWhiteSpace(trigger.Description));
    }

    /// <summary>
    /// The four automatic templates must NOT carry a hand-written description any more. One left
    /// behind would state a condition — "5 days" — beside the team's own setting saying otherwise, and
    /// the reader has no way to tell which is real.
    /// </summary>
    [Theory]
    [InlineData("RegistrationConfirmation")]
    [InlineData("DayBeforeReminder")]
    [InlineData("FccFeeReminder5Day")]
    [InlineData("PaymentExpirationNotice")]
    public void ATemplateARuleSends_HasNoHandWrittenConditionToContradictIt(string key)
    {
        Assert.Null(EmailTemplateTriggers.For(key));
    }

    /// <summary>Every trigger point the engine knows about is one the labels can render — a missing label shows an enum name to an admin.</summary>
    [Fact]
    public void EveryTriggerPoint_HasALabelAndABlurb()
    {
        Assert.All(MessageTriggerDefinitions.All, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(MessageTriggerLabels.Label(definition.Trigger)));
            Assert.False(string.IsNullOrWhiteSpace(MessageTriggerLabels.Blurb(definition.Trigger)));
        });
    }

    /// <summary>And every recipient a rule may legally carry, for the same reason.</summary>
    [Fact]
    public void EveryLegalRecipient_HasALabel()
    {
        Assert.All(MessageTriggerDefinitions.All.SelectMany(d => d.LegalRecipients).Distinct(),
            recipient => Assert.NotEqual(recipient.ToString(), MessageTriggerLabels.Label(recipient)));
    }

    /// <summary>
    /// Hours read back in the unit the form takes, so a rule reads back the way it was written — days,
    /// down to the half day the form's own step allows. Anything finer stays in hours: nothing can
    /// enter one now, and rendering 40 hours as "1.7 days" would be a rounding the list is not
    /// entitled to make. See MessageDelayTests for the boundary itself.
    /// </summary>
    [Theory]
    [InlineData(null, "immediately")]
    [InlineData(1, "1 hour")]
    [InlineData(24, "1 day")]
    [InlineData(120, "5 days")]
    [InlineData(240, "10 days")]
    [InlineData(12, "half a day")]
    [InlineData(36, "1½ days")]
    [InlineData(40, "40 hours")]
    public void DescribeHours_ReadsAsSomebodyWouldSayIt(int? hours, string expected)
    {
        Assert.Equal(expected, MessageTriggerLabels.DescribeHours(hours));
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

    /// <summary>And a retired key must no longer be seeded, as a template or as a rule's target.</summary>
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

    /// <summary>
    /// The genuinely surprising one, and now a rule's field rather than prose: the unpaid-payment
    /// notice goes to the team, not the candidate. Pinned on the definition, because that is what the
    /// admin form offers and therefore what somebody can pick.
    /// </summary>
    [Fact]
    public void PaymentUnpaid_CanAddressTheTeamsOwnInbox()
    {
        Assert.Contains(MessageRecipient.TeamAdminAddress,
            MessageTriggerDefinitions.For(MessageTrigger.PaymentUnpaid).LegalRecipients);
    }

    /// <summary>
    /// ⚠️ <b>This used to assert the opposite</b>, on the reasoning that a registration confirmation
    /// is written to a candidate so offering it to the team inbox offers a mistake. The decided
    /// trigger × recipient matrix overturns that (Mike, 2026-08-20): staff recipients are legal on
    /// <i>every</i> trigger, because "somebody might be crazy and want an email at every single point
    /// along the way to go to themselves". Sending a candidate-worded message to staff is a wording
    /// problem the team owns, not a data-protection one — the message is about their own candidate.
    ///
    /// <para>See <c>docs/trigger-recipient-matrix.md</c>. What is <i>not</i> overturned is below.</para>
    /// </summary>
    [Fact]
    public void CandidateFacingTriggers_MayNowBeAddressedToStaff()
    {
        Assert.Contains(MessageRecipient.TeamAdminAddress,
            MessageTriggerDefinitions.For(MessageTrigger.CandidateRegistered).LegalRecipients);
    }

    /// <summary>
    /// The restriction that survived, and the one that was actually load-bearing: a candidate-facing
    /// trigger still cannot be posted into a Discord channel. Staff receiving a message about their
    /// own candidate is internal; a channel is a room, and the matrix marks the Discord column N for
    /// every trigger except the session reminder.
    /// </summary>
    [Fact]
    public void CandidateFacingTriggers_StillCannotBePostedToAChannel()
    {
        Assert.DoesNotContain(MessageRecipient.DiscordChannel,
            MessageTriggerDefinitions.For(MessageTrigger.CandidateRegistered).LegalRecipients);
    }
}
