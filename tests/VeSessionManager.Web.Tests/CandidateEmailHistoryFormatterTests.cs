using VeSessionManager.Core.Entities;
using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// What a candidate has actually received (#415). The history used to read the legacy
/// <c>Candidate.*SentUtc</c> columns, which is a fixed set of app-defined names — the opposite of what
/// #401 is for — and it failed in three separate ways, one test each below.
/// </summary>
public class CandidateEmailHistoryFormatterTests
{
    private static readonly DateTime Base = new(2026, 8, 18, 15, 0, 0, DateTimeKind.Utc);

    private static Candidate Candidate() => new() { Name = "Ana Diaz", SessionId = 1 };

    private static RuleSend Send(string label, int minutes, MessageTrigger trigger = MessageTrigger.BeforeSessionStart) =>
        new(label, Base.AddMinutes(minutes), trigger);

    /// <summary>
    /// <b>The reported bug.</b> A rule on <c>CandidateTested</c> has no legacy column and never will —
    /// so its mail was sent and the candidate's page showed nothing.
    /// </summary>
    [Fact]
    public void ASendOnATriggerWithNoLegacyColumn_Appears()
    {
        var history = CandidateEmailHistoryFormatter.Build(
            Candidate(), [Send("Congratulations on testing", 0, MessageTrigger.CandidateTested)]);

        Assert.Equal("Congratulations on testing", Assert.Single(history).Label);
    }

    /// <summary>
    /// <b>Two rules on one trigger no longer collapse.</b> Both used to write
    /// <c>DayBeforeReminderSentUtc</c>, so the page showed one line carrying whichever timestamp
    /// landed last — a team configured two sends and could see one.
    /// </summary>
    [Fact]
    public void TwoRulesOnOneTrigger_AppearSeparately_NamedByRule()
    {
        var history = CandidateEmailHistoryFormatter.Build(
            Candidate(),
            [Send("Reminder a week out", 0), Send("Reminder the day before", 60)]);

        Assert.Equal(2, history.Count);
        Assert.Equal("Reminder a week out", history[0].Label);
        Assert.Equal("Reminder the day before", history[1].Label);
    }

    /// <summary>Everything in one list in send order, whatever produced it — a reader wants a timeline, not a grouping by mechanism.</summary>
    [Fact]
    public void RuleSendsAndHandComposedSends_InterleaveByTime()
    {
        var candidate = Candidate();
        candidate.EmailSends.Add(new CandidateEmailSend
        {
            TemplateLabel = "Getting started locally", SentUtc = Base.AddMinutes(30), CandidateId = 1
        });

        var history = CandidateEmailHistoryFormatter.Build(
            candidate, [Send("Registration confirmation", 0), Send("Reminder the day before", 60)]);

        Assert.Equal(
            ["Registration confirmation", "Getting started locally", "Reminder the day before"],
            history.Select(h => h.Label));
    }




    /// <summary>
    /// <b>#417.</b> A hand-send is a run now, so it needs no column and no fallback — including the
    /// Youth Program instructions, which no trigger point can send and which therefore records the
    /// <c>SentByHand</c> marker rather than a real trigger.
    /// </summary>
    [Fact]
    public void HandSends_AppearAsRunsLikeEverythingElse()
    {
        var history = CandidateEmailHistoryFormatter.Build(
            Candidate(),
            [Send("Youth Program instructions", 0, MessageTrigger.SentByHand),
             Send("Registration confirmation (resent)", 10, MessageTrigger.CandidateRegistered)]);

        Assert.Equal(
            ["Youth Program instructions", "Registration confirmation (resent)"],
            history.Select(h => h.Label));
    }

    /// <summary>
    /// The felony instructions can come from the button or from a rule, and both now record a run —
    /// so the one thing that must not happen is the same email appearing twice. It used to take a
    /// dedup rule; now it takes nothing, because there is only one source.
    /// </summary>
    [Fact]
    public void FelonyInstructions_AppearOnce_WhicheverPathSentThem()
    {
        var candidate = Candidate();
        candidate.FelonyDisclosureInstructionsSentUtc = Base;

        var history = CandidateEmailHistoryFormatter.Build(
            candidate, [Send("Felony disclosure instructions", 0, MessageTrigger.FelonyDisclosureDeclared)]);

        Assert.Equal("Felony disclosure instructions", Assert.Single(history).Label);
    }

    [Fact]
    public void NothingSent_IsAnEmptyList()
    {
        Assert.Empty(CandidateEmailHistoryFormatter.Build(Candidate(), []));
    }
}
