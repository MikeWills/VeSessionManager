using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The per-team integration switches (#64), and specifically the distinction the issue calls "the
/// hard part": <b>disabled is not unconfigured</b>.
///
/// <para>Unconfigured means an admin has not finished setup — skip quietly, retry every poll, so
/// adding credentials backfills automatically. Disabled means deliberate and indefinite — suppress,
/// settle, log once, never retry. Reusing the unconfigured pattern would make a muted integration
/// re-attempt and re-log forever and never settle, and a dev team would emit a log line every tick
/// about something switched off on purpose.</para>
///
/// <para>The issue asks for all three halves to be pinned: no calls made, no re-attempt next tick,
/// and no repeated log line. The first two are properties of the services (see
/// <c>TeamIntegrationSwitchEnforcementTests</c>); the third belongs here, to the thing that decides.</para>
/// </summary>
public class TeamIntegrationSwitchTests
{
    /// <summary>Captures what was logged, so "once, not per tick" is checkable rather than asserted.</summary>
    private sealed class RecordingLogger : ILogger<TeamIntegrationState>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, ex));
    }

    private static Team Team(bool overrides, bool zoom = true, bool email = true) => new()
    {
        Id = 1,
        Name = "HRCC",
        IntegrationOverridesEnabled = overrides,
        ZoomEnabled = zoom,
        EmailEnabled = email
    };

    /// <summary>
    /// The default for every existing team, and the property the master switch exists for: with it
    /// off, the individual switches do not apply <b>at all</b>.
    ///
    /// <para>Without that rule a switch left off from an old testing session stays hidden behind a
    /// collapsed panel and silently mutes a team that has since gone into production.</para>
    /// </summary>
    [Fact]
    public void MasterOff_MeansEveryIntegrationIsOn_WhateverTheIndividualSwitchesSay()
    {
        var team = Team(overrides: false, zoom: false, email: false);

        Assert.True(team.IsEnabled(TeamIntegration.Zoom));
        Assert.True(team.IsEnabled(TeamIntegration.Email));
        Assert.Empty(team.MutedIntegrations);
    }

    /// <summary>And the recovery path: one action restores full normal operation.</summary>
    [Fact]
    public void TurningTheMasterOffRestoresEverythingInOneAction()
    {
        var team = Team(overrides: true, zoom: false, email: false);
        Assert.Equal([TeamIntegration.Zoom, TeamIntegration.Email], team.MutedIntegrations);

        team.IntegrationOverridesEnabled = false;

        Assert.Empty(team.MutedIntegrations);
    }

    [Fact]
    public void MasterOn_AppliesTheIndividualSwitches()
    {
        var team = Team(overrides: true, zoom: false);

        Assert.False(team.IsEnabled(TeamIntegration.Zoom));
        Assert.True(team.IsEnabled(TeamIntegration.Discord));
        Assert.Contains(TeamIntegration.Zoom, team.MutedIntegrations);
        Assert.DoesNotContain(TeamIntegration.Discord, team.MutedIntegrations);
    }

    /// <summary>
    /// The third half of the property, and the reason this class exists: a muted integration says so
    /// <b>once</b>, not every poll. A dev team polled hourly would otherwise produce a log line an
    /// hour about something that is off on purpose, which trains people to ignore the log.
    /// </summary>
    [Fact]
    public void AMutedIntegrationIsLoggedOnce_NotOnEveryPoll()
    {
        var logger = new RecordingLogger();
        var state = new TeamIntegrationState(logger);
        var team = Team(overrides: true, zoom: false);

        for (var poll = 0; poll < 20; poll++)
        {
            Assert.False(state.ShouldCall(team, TeamIntegration.Zoom, "creating a Zoom meeting"));
        }

        var line = Assert.Single(logger.Messages);
        Assert.Contains("Zoom", line);
        Assert.Contains("suppressing creating a Zoom meeting", line);
    }

    /// <summary>An integration that is on says nothing at all — the quiet case must stay quiet.</summary>
    [Fact]
    public void AnEnabledIntegrationLogsNothing()
    {
        var logger = new RecordingLogger();
        var state = new TeamIntegrationState(logger);
        var team = Team(overrides: false);

        for (var poll = 0; poll < 5; poll++)
        {
            Assert.True(state.ShouldCall(team, TeamIntegration.Zoom, "creating a Zoom meeting"));
        }

        Assert.Empty(logger.Messages);
    }

    /// <summary>
    /// Re-enabling is also a transition and also says so — an admin who flips a switch back gets
    /// confirmation in the log that it took effect, rather than silence that looks identical to the
    /// change not having applied.
    /// </summary>
    [Fact]
    public void FlippingASwitchBackOnIsLoggedToo_AndOnlyOnce()
    {
        var logger = new RecordingLogger();
        var state = new TeamIntegrationState(logger);
        var team = Team(overrides: true, zoom: false);

        state.ShouldCall(team, TeamIntegration.Zoom, "creating a Zoom meeting");
        state.ShouldCall(team, TeamIntegration.Zoom, "creating a Zoom meeting");

        team.ZoomEnabled = true;
        for (var poll = 0; poll < 5; poll++)
        {
            Assert.True(state.ShouldCall(team, TeamIntegration.Zoom, "creating a Zoom meeting"));
        }

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("switched back on", logger.Messages[1]);
    }

    /// <summary>Teams are tracked independently — muting one must not silence another's message.</summary>
    [Fact]
    public void EachTeamIsTrackedSeparately()
    {
        var logger = new RecordingLogger();
        var state = new TeamIntegrationState(logger);

        var first = Team(overrides: true, zoom: false);
        var second = new Team { Id = 2, Name = "MARC", IntegrationOverridesEnabled = true, ZoomEnabled = false };

        state.ShouldCall(first, TeamIntegration.Zoom, "creating a Zoom meeting");
        state.ShouldCall(second, TeamIntegration.Zoom, "creating a Zoom meeting");

        Assert.Equal(2, logger.Messages.Count);
    }

    /// <summary>And so are integrations within one team — muting Zoom must not silence Discord.</summary>
    [Fact]
    public void EachIntegrationIsTrackedSeparately()
    {
        var logger = new RecordingLogger();
        var state = new TeamIntegrationState(logger);
        var team = new Team { Id = 1, Name = "HRCC", IntegrationOverridesEnabled = true, ZoomEnabled = false, DiscordEnabled = false };

        state.ShouldCall(team, TeamIntegration.Zoom, "creating a Zoom meeting");
        state.ShouldCall(team, TeamIntegration.Discord, "creating a Discord event");

        Assert.Equal(2, logger.Messages.Count);
    }

    /// <summary>
    /// A brand-new team is fully enabled. Asserted against the entity's own defaults rather than the
    /// database's, because both matter and they were briefly different: the generated migration
    /// defaulted the four switches to false while the C# initializers said true, which is invisible
    /// while the master is off and mutes everything the moment it is turned on.
    /// </summary>
    [Fact]
    public void ANewTeamHasEveryIntegrationEnabled()
    {
        var team = new Team { Name = "New", ExamToolsTeamCode = "NEW" };

        Assert.False(team.IntegrationOverridesEnabled);
        Assert.True(team.ZoomEnabled);
        Assert.True(team.DiscordEnabled);
        Assert.True(team.SquareEnabled);
        Assert.True(team.EmailEnabled);
        Assert.Empty(team.MutedIntegrations);
    }
}
