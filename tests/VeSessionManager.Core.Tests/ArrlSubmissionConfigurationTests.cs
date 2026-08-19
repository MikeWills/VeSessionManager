using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// When a team is set up well enough to file a session with ARRL-VEC (issue #197).
///
/// <para>Follows the <c>IsXConfigured</c> convention: "an admin actually did something", never "a
/// shipped default happens to be non-empty". None of these columns has a default, deliberately —
/// `Remote Online` is right for both of Mike's teams and would still be the wrong thing to bake in,
/// because a team that meets in person and never opened the screen would then file "Remote Online"
/// with ARRL and nothing anywhere would look broken.</para>
/// </summary>
public class ArrlSubmissionConfigurationTests
{
    private static Team Configured() => new()
    {
        Name = "MARC",
        ArrlSubmissionLocation = "Remote Online",
        ArrlSubmissionPaymentMethod = ArrlPaymentMethod.CreditCardOnFile,
        ArrlSubmissionEmailSource = ArrlSubmissionEmailSource.SessionLead
    };

    [Fact]
    public void AllThreeRequiredValues_IsConfigured()
    {
        Assert.True(Configured().IsArrlSubmissionConfigured);
    }

    /// <summary>
    /// The real MARC submission carries an empty postfix and an empty Notes field — see the receipt on
    /// #197. So blank is a legitimate, fully-configured answer for both, and neither may be treated as
    /// "not set up yet".
    /// </summary>
    [Fact]
    public void ABlankPostfixAndBlankNote_AreStillConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionNamePostfix = null;
        team.ArrlSubmissionNote = null;

        Assert.True(team.IsArrlSubmissionConfigured);
    }

    [Fact]
    public void WithNoLocation_IsNotConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionLocation = null;

        Assert.False(team.IsArrlSubmissionConfigured);
    }

    [Fact]
    public void WithAWhitespaceLocation_IsNotConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionLocation = "   ";

        Assert.False(team.IsArrlSubmissionConfigured);
    }

    [Fact]
    public void WithNoPaymentMethod_IsNotConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionPaymentMethod = null;

        Assert.False(team.IsArrlSubmissionConfigured);
    }

    /// <summary>
    /// Null rather than defaulting to <see cref="ArrlSubmissionEmailSource.SessionLead"/>: a team
    /// deliberately using the lead's address and a team that never opened the screen must not be the
    /// same row.
    /// </summary>
    [Fact]
    public void WithNoEmailSource_IsNotConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionEmailSource = null;

        Assert.False(team.IsArrlSubmissionConfigured);
    }

    [Fact]
    public void TeamAddressWithoutAnAddress_IsNotConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionEmailSource = ArrlSubmissionEmailSource.TeamAddress;

        Assert.False(team.IsArrlSubmissionConfigured);
    }

    [Fact]
    public void TeamAddressWithAnAddress_IsConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionEmailSource = ArrlSubmissionEmailSource.TeamAddress;
        team.ArrlSubmissionEmail = "ve@marcradio.org";

        Assert.True(team.IsArrlSubmissionConfigured);
    }

    /// <summary>An address left behind from an earlier setting is ignored, not treated as a conflict.</summary>
    [Fact]
    public void SessionLeadWithAStrayAddress_IsStillConfigured()
    {
        var team = Configured();
        team.ArrlSubmissionEmail = "left-over@example.org";

        Assert.True(team.IsArrlSubmissionConfigured);
    }
}
