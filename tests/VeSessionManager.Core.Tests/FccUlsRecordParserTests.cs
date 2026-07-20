using VeSessionManager.Core.FccUls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Fixture-based tests for the pipe-delimited HD.dat/EN.dat join — no live download needed, per
/// the spec's Phase 5 testing requirement. Field positions match those verified against real
/// downloaded FCC ULS data (see docs/fcc-uls-watcher.md), not just the source PDF.
/// </summary>
public class FccUlsRecordParserTests
{
    private static string BuildHdRow(string usi, string? callSign = null, string? licenseStatus = null, string? grantDate = null, string? lastActionDate = null)
    {
        var fields = new string[44];
        Array.Fill(fields, string.Empty);
        fields[0] = "HD";
        fields[1] = usi;
        if (callSign is not null) fields[4] = callSign;
        if (licenseStatus is not null) fields[5] = licenseStatus;
        if (grantDate is not null) fields[7] = grantDate;
        if (lastActionDate is not null) fields[43] = lastActionDate;
        return string.Join('|', fields);
    }

    private static string BuildEnRow(string usi, string? frn = null)
    {
        var fields = new string[23];
        Array.Fill(fields, string.Empty);
        fields[0] = "EN";
        fields[1] = usi;
        if (frn is not null) fields[22] = frn;
        return string.Join('|', fields);
    }

    [Fact]
    public void ParseApplications_JoinsHdAndEnByUsi_UsingLastActionDate()
    {
        var hd = string.Join('\n', [
            BuildHdRow("100", lastActionDate: "07/13/2026"),
            BuildHdRow("200", lastActionDate: "07/12/2026"),
        ]);
        var en = string.Join('\n', [
            BuildEnRow("100", frn: "0001234567"),
            BuildEnRow("200", frn: "0009876543"),
        ]);

        var result = FccUlsRecordParser.ParseApplications(hd, en);

        Assert.Equal(2, result.Count);
        var first = result.Single(r => r.UniqueSystemIdentifier == "100");
        Assert.Equal("0001234567", first.Frn);
        Assert.Equal(new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc), first.LastActionDateUtc);
    }

    [Fact]
    public void ParseApplications_RowWithNoMatchingEnRecord_IsExcluded()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("999", frn: "0001234567"); // different USI, no join

        var result = FccUlsRecordParser.ParseApplications(hd, en);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseApplications_RowWithNoLastActionDate_IsExcluded()
    {
        var hd = BuildHdRow("100"); // no last action date
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseApplications(hd, en);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseApplications_UnparsableDate_IsExcluded()
    {
        var hd = BuildHdRow("100", lastActionDate: "not-a-date");
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseApplications(hd, en);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseApplications_IgnoresNonHdLinesInHdContent()
    {
        var hd = string.Join('\n', [
            "AM|100|||K0BFR|E",
            BuildHdRow("100", lastActionDate: "07/13/2026"),
        ]);
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseApplications(hd, en);

        Assert.Single(result);
    }

    [Fact]
    public void ParseLicenses_JoinsHdAndEnByUsi_WithCallSignStatusAndGrantDate()
    {
        var hd = BuildHdRow("100", callSign: "K0BFR", licenseStatus: "A", grantDate: "01/18/2017");
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseLicenses(hd, en);

        var record = Assert.Single(result);
        Assert.Equal("0001234567", record.Frn);
        Assert.Equal("K0BFR", record.CallSign);
        Assert.Equal("A", record.LicenseStatus);
        Assert.Equal(new DateTime(2017, 1, 18, 0, 0, 0, DateTimeKind.Utc), record.GrantDateUtc);
    }

    [Fact]
    public void ParseLicenses_PreservesCanceledStatus_ForCallerToFilter()
    {
        var hd = BuildHdRow("100", callSign: "K0BFR", licenseStatus: "C", grantDate: "01/18/2017");
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseLicenses(hd, en);

        // Parser itself is a dumb join — it's FccUlsWatcherService's job to decide "C" doesn't
        // count as a grant, not the parser's.
        var record = Assert.Single(result);
        Assert.Equal("C", record.LicenseStatus);
    }

    [Fact]
    public void ParseLicenses_MissingCallSignOrGrantDate_IsExcluded()
    {
        var hd = BuildHdRow("100", licenseStatus: "A"); // no call sign, no grant date
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseLicenses(hd, en);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseLicenses_NoMatchingEnRecord_IsExcluded()
    {
        var hd = BuildHdRow("100", callSign: "K0BFR", licenseStatus: "A", grantDate: "01/18/2017");
        var en = BuildEnRow("999", frn: "0001234567");

        var result = FccUlsRecordParser.ParseLicenses(hd, en);

        Assert.Empty(result);
    }
}
