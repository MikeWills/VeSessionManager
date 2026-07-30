using System.Globalization;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.FccUls;

/// <summary>
/// Parses the pipe-delimited HD.dat/EN.dat text extracted from an FCC ULS zip and joins them by
/// Unique System Identifier (present on both record types). Pure string-in/records-out — no HTTP,
/// no zip handling — so the join logic is directly unit-testable against small fixture strings,
/// per the spec's Phase 5 testing requirement.
///
/// Field positions below are 1-based, matching the FCC's own "Position" column
/// (https://www.fcc.gov/file/13762/download), but verified directly against real downloaded
/// daily files rather than trusted as printed: the source PDF's text extraction turned out to be
/// off by one for EN (it lists FRN at position 24; real EN.dat rows have it at position 23,
/// confirmed by matching a real 10-digit FRN value). HD's positions matched the doc exactly. See
/// docs/fcc-uls-watcher.md for the verification data.
/// </summary>
public static class FccUlsRecordParser
{
    private const int HdUsiField = 2;
    private const int HdCallSignField = 5;
    private const int HdLicenseStatusField = 6;
    private const int HdGrantDateField = 8;
    private const int HdLastActionDateField = 44;

    private const int EnUsiField = 2;
    private const int EnFrnField = 23;

    private const int HsUsiField = 2;
    private const int HsCodeField = 6;

    private const string RedLightOffCode = "RDLOFF";
    private const string RedLightCompleteCode = "RDLCOM";
    private const string BasicQualificationOffCode = "BQOFF";
    private const string BasicQualificationCompleteCode = "BQCOM";
    private const string PaymentOffCode = "FVPOFF";
    private const string PaymentConfirmedCode = "FVPCNF";
    private const string PaymentVerificationCompleteCode = "FVPCOM";

    /// <summary>hsContent is optional — a caller that doesn't have History data (or is only after license records) can omit it and every application simply comes back with HoldReason None / PaymentStatus Unknown.</summary>
    public static IReadOnlyList<FccUlsApplicationRecord> ParseApplications(string hdContent, string enContent, string? hsContent = null)
    {
        var frnByUsi = ParseFrnByUsi(enContent);
        var signalsByUsi = ParseHistorySignalsByUsi(hsContent);
        var results = new List<FccUlsApplicationRecord>();

        foreach (var row in ParseRows(hdContent, "HD"))
        {
            var usi = Field(row, HdUsiField);
            if (usi is null || !frnByUsi.TryGetValue(usi, out var frn))
            {
                continue;
            }

            var lastActionDate = ParseDate(Field(row, HdLastActionDateField));
            if (lastActionDate is null)
            {
                continue;
            }

            var (holdReason, paymentStatus) = signalsByUsi.GetValueOrDefault(usi, (FccApplicationHoldReason.None, FccApplicationPaymentStatus.Unknown));
            results.Add(new FccUlsApplicationRecord(usi, frn, lastActionDate.Value, holdReason, paymentStatus));
        }

        return results;
    }

    /// <summary>
    /// HS.dat's History rows are keyed by Unique System Identifier + a day-granularity Log Date (no
    /// time component), so same-day rows aren't reliably orderable by date alone — this walks each
    /// USI's rows in the file's own natural (already-chronological) order instead of re-sorting by
    /// Log Date, toggling each of the three OFF/COM code pairs (Red Light, Basic Qualification, fee
    /// Payment Verification) as it's encountered. Codes are FCC's own documented values (see
    /// uls_code_definitions), not guessed from data correlation.
    /// </summary>
    private static Dictionary<string, (FccApplicationHoldReason HoldReason, FccApplicationPaymentStatus PaymentStatus)> ParseHistorySignalsByUsi(string? hsContent)
    {
        var result = new Dictionary<string, (FccApplicationHoldReason, FccApplicationPaymentStatus)>();
        if (hsContent is null)
        {
            return result;
        }

        var redLightHeldByUsi = new Dictionary<string, bool>();
        var basicQualificationHeldByUsi = new Dictionary<string, bool>();
        var paymentPendingByUsi = new Dictionary<string, bool>();

        foreach (var row in ParseRows(hsContent, "HS"))
        {
            var usi = Field(row, HsUsiField);
            var code = Field(row, HsCodeField);
            if (usi is null || code is null)
            {
                continue;
            }

            switch (code)
            {
                case RedLightOffCode:
                    redLightHeldByUsi[usi] = true;
                    break;
                case RedLightCompleteCode:
                    redLightHeldByUsi[usi] = false;
                    break;
                case BasicQualificationOffCode:
                    basicQualificationHeldByUsi[usi] = true;
                    break;
                case BasicQualificationCompleteCode:
                    basicQualificationHeldByUsi[usi] = false;
                    break;
                case PaymentOffCode:
                    paymentPendingByUsi[usi] = true;
                    break;
                case PaymentConfirmedCode:
                case PaymentVerificationCompleteCode:
                    paymentPendingByUsi[usi] = false;
                    break;
            }
        }

        foreach (var usi in redLightHeldByUsi.Keys.Union(basicQualificationHeldByUsi.Keys).Union(paymentPendingByUsi.Keys))
        {
            var redLightHeld = redLightHeldByUsi.GetValueOrDefault(usi);
            var basicQualificationHeld = basicQualificationHeldByUsi.GetValueOrDefault(usi);
            var holdReason = (redLightHeld, basicQualificationHeld) switch
            {
                (true, true) => FccApplicationHoldReason.RedLightAndBasicQualification,
                (true, false) => FccApplicationHoldReason.RedLight,
                (false, true) => FccApplicationHoldReason.BasicQualification,
                (false, false) => FccApplicationHoldReason.None
            };

            var paymentStatus = paymentPendingByUsi.TryGetValue(usi, out var pending)
                ? (pending ? FccApplicationPaymentStatus.PendingVerification : FccApplicationPaymentStatus.Paid)
                : FccApplicationPaymentStatus.Unknown;

            result[usi] = (holdReason, paymentStatus);
        }

        return result;
    }

    public static IReadOnlyList<FccUlsLicenseRecord> ParseLicenses(string hdContent, string enContent)
    {
        var frnByUsi = ParseFrnByUsi(enContent);
        var results = new List<FccUlsLicenseRecord>();

        foreach (var row in ParseRows(hdContent, "HD"))
        {
            var usi = Field(row, HdUsiField);
            if (usi is null || !frnByUsi.TryGetValue(usi, out var frn))
            {
                continue;
            }

            var callSign = Field(row, HdCallSignField);
            var licenseStatus = Field(row, HdLicenseStatusField);
            var grantDate = ParseDate(Field(row, HdGrantDateField));
            if (callSign is null || licenseStatus is null || grantDate is null)
            {
                continue;
            }

            results.Add(new FccUlsLicenseRecord(usi, frn, callSign, licenseStatus, grantDate.Value));
        }

        return results;
    }

    private static Dictionary<string, string> ParseFrnByUsi(string enContent)
    {
        var result = new Dictionary<string, string>();
        foreach (var row in ParseRows(enContent, "EN"))
        {
            var usi = Field(row, EnUsiField);
            var frn = Field(row, EnFrnField);
            if (usi is not null && frn is not null)
            {
                result[usi] = frn;
            }
        }

        return result;
    }

    private static List<string[]> ParseRows(string content, string expectedRecordType)
    {
        var rows = new List<string[]>();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length > 0 && fields[0] == expectedRecordType)
            {
                rows.Add(fields);
            }
        }

        return rows;
    }

    private static string? Field(string[] fields, int oneBasedPosition)
    {
        var index = oneBasedPosition - 1;
        if (index < 0 || index >= fields.Length)
        {
            return null;
        }

        var value = fields[index].Trim();
        return value.Length == 0 ? null : value;
    }

    private static DateTime? ParseDate(string? value) =>
        value is not null
        && DateTime.TryParseExact(value, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
}
