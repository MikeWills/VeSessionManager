using VeSessionManager.Core.Entities;
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

    /// <summary>AM.dat (Amateur) — positions confirmed against a real l_am_thu.zip on 2026-07-30: USI at index 1, operator class at 5, previous operator class at 15/16.</summary>
    private static string BuildAmRow(string usi, string? operatorClass = null)
    {
        var fields = new string[18];
        Array.Fill(fields, string.Empty);
        fields[0] = "AM";
        fields[1] = usi;
        if (operatorClass is not null) fields[5] = operatorClass;
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

    private static string BuildHsRow(string usi, string code)
    {
        var fields = new string[6];
        Array.Fill(fields, string.Empty);
        fields[0] = "HS";
        fields[1] = usi;
        fields[5] = code;
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
    public void ParseApplications_NoHsContent_HoldReasonDefaultsNone()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseApplications(hd, en);

        Assert.Equal(FccApplicationHoldReason.None, Assert.Single(result).HoldReason);
    }

    [Fact]
    public void ParseApplications_HsRedLightOff_SetsHoldReasonRedLight()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = BuildHsRow("100", "RDLOFF");

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        Assert.Equal(FccApplicationHoldReason.RedLight, Assert.Single(result).HoldReason);
    }

    [Fact]
    public void ParseApplications_HsRedLightOffThenCompleted_HoldReasonIsNone()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = string.Join('\n', [BuildHsRow("100", "RDLOFF"), BuildHsRow("100", "RDLCOM")]);

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        Assert.Equal(FccApplicationHoldReason.None, Assert.Single(result).HoldReason);
    }

    [Fact]
    public void ParseApplications_HsBasicQualificationOff_SetsHoldReasonBasicQualification()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = BuildHsRow("100", "BQOFF");

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        Assert.Equal(FccApplicationHoldReason.BasicQualification, Assert.Single(result).HoldReason);
    }

    [Fact]
    public void ParseApplications_BothHoldsActive_SetsHoldReasonRedLightAndBasicQualification()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = string.Join('\n', [BuildHsRow("100", "RDLOFF"), BuildHsRow("100", "BQOFF")]);

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        Assert.Equal(FccApplicationHoldReason.RedLightAndBasicQualification, Assert.Single(result).HoldReason);
    }

    [Fact]
    public void ParseApplications_HsRowForDifferentUsi_DoesNotAffectThisRecord()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = BuildHsRow("999", "RDLOFF"); // different USI

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        Assert.Equal(FccApplicationHoldReason.None, Assert.Single(result).HoldReason);
    }

    [Fact]
    public void ParseApplications_NoHsContent_PaymentStatusDefaultsUnknown()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");

        var result = FccUlsRecordParser.ParseApplications(hd, en);

        Assert.Equal(FccApplicationPaymentStatus.Unknown, Assert.Single(result).PaymentStatus);
    }

    [Fact]
    public void ParseApplications_HsPaymentOffline_SetsPaymentStatusPendingVerification()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = BuildHsRow("100", "FVPOFF");

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        Assert.Equal(FccApplicationPaymentStatus.PendingVerification, Assert.Single(result).PaymentStatus);
    }

    [Theory]
    [InlineData("FVPCNF")]
    [InlineData("FVPCOM")]
    public void ParseApplications_HsPaymentOfflineThenResolved_SetsPaymentStatusPaid(string resolvingCode)
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = string.Join('\n', [BuildHsRow("100", "FVPOFF"), BuildHsRow("100", resolvingCode)]);

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        Assert.Equal(FccApplicationPaymentStatus.Paid, Assert.Single(result).PaymentStatus);
    }

    [Fact]
    public void ParseApplications_HoldAndPaymentSignals_AreIndependent()
    {
        var hd = BuildHdRow("100", lastActionDate: "07/13/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var hs = string.Join('\n', [BuildHsRow("100", "RDLOFF"), BuildHsRow("100", "FVPCNF")]);

        var result = FccUlsRecordParser.ParseApplications(hd, en, hs);

        var record = Assert.Single(result);
        Assert.Equal(FccApplicationHoldReason.RedLight, record.HoldReason);
        Assert.Equal(FccApplicationPaymentStatus.Paid, record.PaymentStatus);
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

    // ---- AM.dat operator-class join (2026-07-30) ----

    [Theory]
    [InlineData("T", LicenseClass.Technician)]
    [InlineData("G", LicenseClass.General)]
    [InlineData("E", LicenseClass.Extra)]
    public void ParseLicenses_JoinsOperatorClassFromAm(string code, LicenseClass expected)
    {
        var hd = BuildHdRow("100", callSign: "K0BFR", licenseStatus: "A", grantDate: "01/18/2017", lastActionDate: "07/21/2026");
        var en = BuildEnRow("100", frn: "0001234567");
        var am = BuildAmRow("100", code);

        var record = Assert.Single(FccUlsRecordParser.ParseLicenses(hd, en, am));

        Assert.Equal(expected, record.OperatorClass);
    }

    [Theory]
    [InlineData("A")] // Advanced — closed to new issues since 2000
    [InlineData("N")] // Novice — likewise
    [InlineData("")]
    public void ParseLicenses_LegacyOrBlankOperatorClass_MapsToNone(string code)
    {
        // Deliberate: these can only ever be a class someone walked in WITH, so mapping them to None
        // means they can never equal a candidate's NewLicenseClass and never confirm an upgrade.
        var hd = BuildHdRow("100", callSign: "K0BFR", licenseStatus: "A", grantDate: "01/18/2017");
        var en = BuildEnRow("100", frn: "0001234567");
        var am = BuildAmRow("100", code);

        var record = Assert.Single(FccUlsRecordParser.ParseLicenses(hd, en, am));

        Assert.Equal(LicenseClass.None, record.OperatorClass);
    }

    [Fact]
    public void ParseLicenses_NoAmContent_YieldsOperatorClassNone_ButStillParses()
    {
        var hd = BuildHdRow("100", callSign: "K0BFR", licenseStatus: "A", grantDate: "01/18/2017");
        var en = BuildEnRow("100", frn: "0001234567");

        var record = Assert.Single(FccUlsRecordParser.ParseLicenses(hd, en, amContent: null));

        Assert.Equal(LicenseClass.None, record.OperatorClass);
        Assert.Equal("K0BFR", record.CallSign);
    }

    [Fact]
    public void ParseLicenses_CapturesLastActionDate_SeparatelyFromGrantDate()
    {
        // The upgrade case: FCC pins Grant Date to the original license and advances only Last Action.
        var hd = BuildHdRow("100", callSign: "N2LQH", licenseStatus: "A", grantDate: "04/30/2021", lastActionDate: "07/21/2026");
        var en = BuildEnRow("100", frn: "0001234567");

        var record = Assert.Single(FccUlsRecordParser.ParseLicenses(hd, en));

        Assert.Equal(new DateTime(2021, 4, 30, 0, 0, 0, DateTimeKind.Utc), record.GrantDateUtc);
        Assert.Equal(new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc), record.LastActionDateUtc);
    }

    [Fact]
    public void ParseLicenses_MissingLastActionDate_FallsBackToGrantDate_RatherThanDroppingTheRow()
    {
        // Regression guard: dropping such a row would remove license records the pre-upgrade parser
        // accepted, breaking the first-time-licensee path this change must leave untouched.
        var hd = BuildHdRow("100", callSign: "K0BFR", licenseStatus: "A", grantDate: "01/18/2017");
        var en = BuildEnRow("100", frn: "0001234567");

        var record = Assert.Single(FccUlsRecordParser.ParseLicenses(hd, en));

        Assert.Equal(record.GrantDateUtc, record.LastActionDateUtc);
    }
}
