using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class IngestionScheduleServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
    private const int NormalIntervalMinutes = 60;

    private static Team CreateTeam(DateTime? lastIngestionRunUtc) =>
        new() { Name = "TESTTEAM", CreatedUtc = Now, LastIngestionRunUtc = lastIngestionRunUtc };

    [Fact]
    public void NeverRunBefore_IsDue_RegardlessOfInterval()
    {
        var team = CreateTeam(lastIngestionRunUtc: null);

        var due = new IngestionScheduleService().IsDue(team, NormalIntervalMinutes, Now);

        Assert.True(due);
    }

    [Fact]
    public void LongPastNormalInterval_IsDue()
    {
        var team = CreateTeam(lastIngestionRunUtc: Now.AddHours(-3));

        var due = new IngestionScheduleService().IsDue(team, NormalIntervalMinutes, Now);

        Assert.True(due);
    }

    [Fact]
    public void JustUnderNormalInterval_IsNotDue()
    {
        var team = CreateTeam(lastIngestionRunUtc: Now.AddMinutes(-59));

        var due = new IngestionScheduleService().IsDue(team, NormalIntervalMinutes, Now);

        Assert.False(due);
    }

    [Fact]
    public void ExactlyAtNormalInterval_IsDue()
    {
        var team = CreateTeam(lastIngestionRunUtc: Now.AddMinutes(-60));

        var due = new IngestionScheduleService().IsDue(team, NormalIntervalMinutes, Now);

        Assert.True(due);
    }
}
