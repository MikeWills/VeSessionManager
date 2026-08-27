using VeSessionManager.Core.Email;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The standalone .ics builder (#491) — no send-path wiring depends on this yet (see the class's own
/// remarks), so these tests are purely about producing a spec-correct file for a real calendar
/// client to parse.
/// </summary>
public class IcsInviteBuilderTests
{
    private static readonly DateTime StartUtc = new(2026, 9, 12, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_ProducesTheRequiredVCalendarAndVEventWrapper()
    {
        var ics = IcsInviteBuilder.Build("session-1", "August Exam Session", StartUtc, 60, null);

        Assert.Contains("BEGIN:VCALENDAR\r\n", ics);
        Assert.Contains("VERSION:2.0\r\n", ics);
        Assert.Contains("BEGIN:VEVENT\r\n", ics);
        Assert.Contains("END:VEVENT\r\n", ics);
        Assert.Contains("END:VCALENDAR\r\n", ics);
    }

    [Fact]
    public void Build_UsesCrlfLineEndings()
    {
        var ics = IcsInviteBuilder.Build("session-1", "Session", StartUtc, 60, null);

        Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
    }

    [Fact]
    public void Build_DtStartAndDtEnd_AreUtcAndReflectDuration()
    {
        var ics = IcsInviteBuilder.Build("session-1", "Session", StartUtc, 90, null);

        Assert.Contains("DTSTART:20260912T020000Z\r\n", ics);
        Assert.Contains("DTEND:20260912T033000Z\r\n", ics);
    }

    [Fact]
    public void Build_IncludesTheTitleAsSummary()
    {
        var ics = IcsInviteBuilder.Build("session-1", "August Exam Session", StartUtc, 60, null);

        Assert.Contains("SUMMARY:August Exam Session\r\n", ics);
    }

    [Fact]
    public void Build_NoLocation_OmitsLocationAndDescriptionEntirely()
    {
        var ics = IcsInviteBuilder.Build("session-1", "Session", StartUtc, 60, null);

        Assert.DoesNotContain("LOCATION:", ics);
        Assert.DoesNotContain("DESCRIPTION:", ics);
    }

    [Fact]
    public void Build_WithLocation_IncludesItAsBothLocationAndDescription()
    {
        var ics = IcsInviteBuilder.Build("session-1", "Session", StartUtc, 60, "https://zoom.us/j/123456");

        Assert.Contains("LOCATION:https://zoom.us/j/123456\r\n", ics);
        Assert.Contains("DESCRIPTION:https://zoom.us/j/123456\r\n", ics);
    }

    /// <summary>Comma, semicolon, and backslash are structural characters in this format (RFC 5545 §3.3.11) — unescaped, a session title containing one would corrupt the file rather than just look odd.</summary>
    [Fact]
    public void Build_EscapesCommaSemicolonAndBackslashInTheTitle()
    {
        var ics = IcsInviteBuilder.Build("session-1", "Session; Room A, Building \\B", StartUtc, 60, null);

        Assert.Contains("SUMMARY:Session\\; Room A\\, Building \\\\B\r\n", ics);
    }

    [Fact]
    public void Build_SameUidTwice_ProducesTheSameUidLine()
    {
        var first = IcsInviteBuilder.Build("session-42", "Session", StartUtc, 60, null);
        var second = IcsInviteBuilder.Build("session-42", "Session", StartUtc, 60, null);

        Assert.Contains("UID:session-42\r\n", first);
        Assert.Contains("UID:session-42\r\n", second);
    }
}
