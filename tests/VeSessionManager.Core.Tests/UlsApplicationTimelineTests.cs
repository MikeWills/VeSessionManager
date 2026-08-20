using System.Text.Json;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #195 — the ULS application timeline. ExamTools' mirror already returns human-readable
/// history entries (<c>code_text: "Redlight Review Completed"</c>) with dates on every lookup the
/// watcher already makes; until now everything but the hold flag was discarded at parse time.
///
/// <para><b>Why this is worth surfacing at all:</b> <c>wireless2.fcc.gov</c> returns Akamai
/// "Access Denied" to this deployment, so there is no "just click through to ULS" fallback for a
/// Session Manager asking "what is actually happening with this application?". This gives them more
/// than the FCC page would, from data already in hand.</para>
/// </summary>
public class UlsApplicationTimelineTests
{
    /// <summary>
    /// Goes through the real JSON attributes and the real mapper — Core exposes internals to this
    /// assembly — rather than stubbing <c>IUlsLookupClient</c>, because the discarded field this
    /// issue is about lived precisely in the mapping a stub would skip over.
    /// </summary>
    private static UlsLookupResult Map(string json) =>
        ExamToolsUlsLookupClient.UlsLookupMapper.Map(
            JsonSerializer.Deserialize<UlsLookupResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

    private const string ResponseWithHistory = """
        {
          "type": "active",
          "u_id": 4321,
          "callsign": "WX0MIK",
          "license_status": "Active",
          "license_class": "technician",
          "pendingApplications": [
            {
              "uls_filenumber": "0012131564",
              "application_purpose": "NE",
              "receipt_date": "2026-08-01T00:00:00",
              "history": [
                { "log_date": "2026-08-01T00:00:00", "code": "RDLOFF", "code_text": "Redlight Review Initiated" },
                { "log_date": "2026-08-04T00:00:00", "code": "RDLCOM", "code_text": "Redlight Review Completed" }
              ]
            }
          ]
        }
        """;

    /// <summary>The mapping this issue is actually about: the text was being thrown away.</summary>
    [Fact]
    public void TheHumanReadableText_IsCarriedThrough_NotDiscarded()
    {
        var history = Assert.Single(Map(ResponseWithHistory).PendingApplications).History;

        Assert.Collection(history,
            first =>
            {
                Assert.Equal("RDLOFF", first.Code);
                Assert.Equal("Redlight Review Initiated", first.CodeText);
                Assert.Equal(new DateTime(2026, 8, 1), first.LogDateUtc);
            },
            second => Assert.Equal("Redlight Review Completed", second.CodeText));
    }

    /// <summary>
    /// The endpoint is undocumented and unauthenticated, so it can change shape without notice — and
    /// this field was observed rather than specified. A missing <c>code_text</c> must degrade to the
    /// raw code, never to a blank row: a timeline entry with no words is worse than one with jargon.
    /// </summary>
    [Fact]
    public void WhenTheTextIsMissing_TheCodeIsUsedAsTheDescription()
    {
        const string noText = """
            {"type":"active","u_id":1,"pendingApplications":[{"uls_filenumber":"1","history":[{"log_date":"2026-08-01T00:00:00","code":"BQCOM"}]}]}
            """;

        var entry = Assert.Single(Assert.Single(Map(noText).PendingApplications).History);

        Assert.Null(entry.CodeText);
        Assert.Equal("BQCOM", entry.Description);
    }

    [Fact]
    public void Description_PrefersTheText_WhenPresent()
        => Assert.Equal("Redlight Review Completed", new UlsHistoryEntry(null, "RDLCOM", "Redlight Review Completed").Description);

    /// <summary>Whitespace-only text is the same problem as missing text, and JSON from a mirror is exactly where that turns up.</summary>
    [Fact]
    public void Description_FallsBackWhenTheTextIsBlank()
        => Assert.Equal("RDLCOM", new UlsHistoryEntry(null, "RDLCOM", "   ").Description);

    /// <summary>
    /// The code is still upper-cased and trimmed — <c>ResolveHoldReason</c> matches on exact codes
    /// (RDLOFF/RDLCOM, BQOFF/BQCOM), so a shape change that returned "rdloff" would silently stop
    /// detecting holds. The text is deliberately left exactly as FCC wrote it.
    /// </summary>
    [Fact]
    public void TheCodeIsStillNormalised_AndTheTextIsLeftAlone()
    {
        const string messy = """
            {"type":"active","u_id":1,"pendingApplications":[{"uls_filenumber":"1","history":[{"log_date":"2026-08-01T00:00:00","code":" rdloff ","code_text":"Redlight Review Initiated"}]}]}
            """;

        var entry = Assert.Single(Assert.Single(Map(messy).PendingApplications).History);

        Assert.Equal("RDLOFF", entry.Code);
        Assert.Equal("Redlight Review Initiated", entry.CodeText);
    }
}
