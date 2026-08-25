using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Manual sends are trigger points too — Mike, 2026-08-21.
///
/// <para><b>The problem this solves.</b> A template's available tags depend on which trigger sends
/// it, but a template was authored separately from any rule, so the editor could not say what was
/// available: it did not know yet. The page showed no tags at all, which is not a missing affordance
/// but an unanswerable question. Mike: <i>"there's no way currently that you can link up a template
/// to the correct rule so that a person can have the right tags available to them."</i></para>
///
/// <para><b>The fix is to make everything a trigger.</b> A hand-composed email is a message whose
/// mechanism is "somebody pressed a button" rather than a scan or a clock. Once
/// <c>ManualToCandidate</c> and <c>ManualToVe</c> exist, nothing can be authored without a trigger —
/// so the tag list is always answerable, everywhere.</para>
/// </summary>
public class ManualTriggerTests
{
    [Theory]
    [InlineData(MessageTrigger.ManualToCandidate)]
    [InlineData(MessageTrigger.ManualToVe)]
    public void AManualTrigger_HasTheManualMechanism(MessageTrigger trigger)
        => Assert.Equal(MessageTriggerMechanism.Manual, MessageTriggerDefinitions.For(trigger).Mechanism);

    /// <summary>
    /// No delay, because there is no anchor to count from — a person chose the moment. That is the
    /// difference between <c>Manual</c> and <c>TimeRelative</c>, and it is what the form keys off to
    /// know not to ask for a number.
    /// </summary>
    [Theory]
    [InlineData(MessageTrigger.ManualToCandidate)]
    [InlineData(MessageTrigger.ManualToVe)]
    public void AManualTrigger_TakesNoDelay(MessageTrigger trigger)
        => Assert.Null(MessageTriggerDefinitions.For(trigger).DefaultParameterHours);

    /// <summary>
    /// ⚠️ The tags are the whole point, and they are the ones those screens <b>already</b> supply —
    /// taken from the same <c>Names</c> lists the send paths use, rather than a second list written
    /// out here. Two lists of the same thing is how a tag comes to be offered that renders blank.
    /// </summary>
    [Fact]
    public void ManualToCandidate_OffersExactlyWhatTheCandidateComposeScreenSupplies()
        => Assert.Equal(
            CandidatePlaceholderValues.Names,
            MessageTriggerDefinitions.For(MessageTrigger.ManualToCandidate).Placeholders);

    [Fact]
    public void ManualToVe_OffersExactlyWhatTheVeComposeScreenSupplies()
        => Assert.Equal(
            VolunteerExaminerPlaceholderValues.Names,
            MessageTriggerDefinitions.For(MessageTrigger.ManualToVe).Placeholders);

    /// <summary>
    /// A manual message is addressed at send time — you pick the people on the screen — so the rule
    /// carries no recipient to choose. Distinct from an empty list meaning "nobody may receive this".
    /// </summary>
    [Theory]
    [InlineData(MessageTrigger.ManualToCandidate)]
    [InlineData(MessageTrigger.ManualToVe)]
    public void AManualTrigger_HasNoRecipientToChoose(MessageTrigger trigger)
        => Assert.Empty(MessageTriggerDefinitions.For(trigger).LegalRecipients);

    /// <summary>
    /// ⚠️ The VE one must never be addressable by a scan. Every automated path in this app is
    /// candidate- or payment-subject; a VE-facing message exists only because a person chose to send
    /// it, and a trigger that could fire it on a schedule would be a mail path nobody asked for.
    /// </summary>
    [Fact]
    public void ManualToVe_IsNotScannable()
        => Assert.Equal(MessageTriggerMechanism.Manual, MessageTriggerDefinitions.For(MessageTrigger.ManualToVe).Mechanism);

    /// <summary>
    /// Every trigger a rule can be built on is registered — one the registry does not know is one the
    /// form cannot offer.
    ///
    /// <para><c>SentByHand</c> is excluded deliberately: it is a marker written on a <c>MessageRuleRun</c>
    /// to record that a person sent something outside any rule, not a trigger a rule can use. The
    /// service refuses it as <c>TriggerNotConfigurable</c>.</para>
    ///
    /// <para><c>PaymentUnpaid</c> is excluded too, as of 2026-08-25 — removed from the configurable
    /// list, not the enum, so old <c>MessageRuleRun</c> history naming it still renders. See
    /// <c>MessageTriggerDefinitions.All</c>'s own comment for why.</para>
    /// </summary>
    [Fact]
    public void EveryConfigurableTrigger_IsDefined()
    {
        foreach (var trigger in Enum.GetValues<MessageTrigger>().Where(t => t is not MessageTrigger.SentByHand and not MessageTrigger.PaymentUnpaid))
        {
            Assert.NotNull(MessageTriggerDefinitions.For(trigger));
        }
    }
}

/// <summary>
/// A message owns its own words now, rather than pointing at a template that is authored elsewhere.
/// </summary>
public class MessageRuleContentTests
{
    /// <summary>
    /// The structural change. <c>TemplateKey</c> is gone: the body cannot live somewhere that does
    /// not know which trigger will send it, because that is precisely what made the tag list
    /// unanswerable.
    /// </summary>
    [Fact]
    public void ARule_CarriesItsOwnSubjectAndBody()
    {
        var rule = new MessageRule
        {
            TeamId = 1,
            Name = "Day-before reminder",
            Trigger = MessageTrigger.BeforeSessionStart,
            Subject = "Reminder: your VE exam session is tomorrow",
            Body = "<p>Hello {{CandidateFirstName}}</p>",
            CreatedUtc = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc)
        };

        Assert.Equal("Reminder: your VE exam session is tomorrow", rule.Subject);
        Assert.Contains("{{CandidateFirstName}}", rule.Body);
    }

    /// <summary>
    /// A manual message is offered on a compose screen rather than fired, so "off" means "not offered"
    /// rather than "does not fire". Same column, different sentence — worth stating because the UI has
    /// to say the right one or an off message reads as broken.
    /// </summary>
    [Fact]
    public void AManualMessage_CanBeSwitchedOff_LikeAnyOther()
    {
        var rule = new MessageRule
        {
            TeamId = 1,
            Name = "Getting started locally",
            Trigger = MessageTrigger.ManualToCandidate,
            Subject = "Getting started",
            Body = "text",
            IsEnabled = false,
            CreatedUtc = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc)
        };

        Assert.False(rule.IsEnabled);
    }
}
