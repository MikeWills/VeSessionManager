using System.Text.Json.Serialization;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// One candidate's ULS state as returned by <c>GET /api/uls/lookup2/{frnOrCallsign}</c>, already
/// normalised out of the raw JSON. See docs/uls-watcher.md for the observed response shape and the
/// verification data behind each field's meaning.
/// </summary>
public sealed record UlsLookupResult
{
    /// <summary>False when the endpoint answered <c>type: "notfound"</c> — an unknown FRN is a normal, expected answer (a candidate FCC has never seen), not an error.</summary>
    public bool Found { get; init; }

    /// <summary>ULS "Unique System Identifier" for the *license* — maps to Candidate.FccUlsLicenseKey and powers the FCC ULS deep link.</summary>
    public long? UniqueSystemIdentifier { get; init; }

    public string? CallSign { get; init; }

    /// <summary>"Active", "Pending", … — only "Active" is treated as a grant, matching the old HD License Status "A" rule.</summary>
    public string? LicenseStatus { get; init; }

    /// <summary>Current operator class. Unrecognised/legacy values (Novice, Advanced) map to None, which conservatively means "won't confirm an upgrade" rather than guessing.</summary>
    public LicenseClass OperatorClass { get; init; }

    /// <summary>The class held *before* the current one, when ExamTools reports it. Informational — the upgrade test keys off OperatorClass + EffectiveDateUtc, not this.</summary>
    public LicenseClass PreviousOperatorClass { get; init; }

    /// <summary>Original license grant. Does NOT advance on a class upgrade — FCC pins it to the first issuance (a 2026 upgrade can still report a 2021 grant date).</summary>
    public DateTime? GrantDateUtc { get; init; }

    /// <summary>**Does** advance on an upgrade — ExamTools' rendering of HD's Last Action Date, and the only positive same-record signal that an upgrade actually landed.</summary>
    public DateTime? EffectiveDateUtc { get; init; }

    public IReadOnlyList<UlsPendingApplication> PendingApplications { get; init; } = [];

    public static UlsLookupResult NotFound { get; } = new() { Found = false };
}

public sealed record UlsPendingApplication
{
    public string? UlsFileNumber { get; init; }

    /// <summary>When FCC received the application. Maps to Candidate.ApplicationDateEnteredUtc — verified to equal the value the old HD-Last-Action-Date rule produced for a real candidate.</summary>
    public DateTime? ReceiptDateUtc { get; init; }

    /// <summary>Chronological history entries, each carrying a ULS action code (RDLOFF/RDLCOM, BQOFF/BQCOM, FVPOFF/FVPCNF/FVPCOM …).</summary>
    public IReadOnlyList<UlsHistoryEntry> History { get; init; } = [];
}

public sealed record UlsHistoryEntry(DateTime? LogDateUtc, string Code);

/// <summary>Raw JSON shapes — kept separate from the normalised records above so the mapping stays in one place (UlsLookupMapper) and a shape change surfaces there rather than across the service.</summary>
internal sealed class UlsLookupResponse
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("u_id")] public long? UId { get; set; }
    [JsonPropertyName("callsign")] public string? CallSign { get; set; }
    [JsonPropertyName("license_status")] public string? LicenseStatus { get; set; }
    [JsonPropertyName("license_class")] public string? LicenseClass { get; set; }
    [JsonPropertyName("prev_license_class")] public string? PrevLicenseClass { get; set; }
    [JsonPropertyName("grant_date")] public DateTime? GrantDate { get; set; }
    [JsonPropertyName("effective_date")] public DateTime? EffectiveDate { get; set; }
    [JsonPropertyName("pendingApplications")] public List<UlsPendingApplicationResponse>? PendingApplications { get; set; }
}

internal sealed class UlsPendingApplicationResponse
{
    [JsonPropertyName("uls_filenumber")] public string? UlsFileNumber { get; set; }

    /// <summary>`/lookup2/` names this `uls_filenumber`, but `/lookup/` used `_id` for the same value — accept both so a shape tweak doesn't silently blank the file number.</summary>
    [JsonPropertyName("_id")] public string? LegacyId { get; set; }

    [JsonPropertyName("receipt_date")] public DateTime? ReceiptDate { get; set; }
    [JsonPropertyName("history")] public List<UlsHistoryResponse>? History { get; set; }
}

internal sealed class UlsHistoryResponse
{
    [JsonPropertyName("log_date")] public DateTime? LogDate { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}
