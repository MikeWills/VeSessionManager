using System.Text.Json.Serialization;

namespace VeSessionManager.Core.ExamTools;

// Shapes verified against real examtools.dev responses on 2026-07-19 — see api-examples/ for
// runnable requests. Only the fields ingestion needs are mapped; unknown fields are ignored.

/// <summary>One item from GET /api/veUser/sessions?team=... (also the shape of the single-session detail).</summary>
public class ExamToolsSession
{
    [JsonPropertyName("_id")]
    public required string Id { get; set; }

    /// <summary>UTC start time.</summary>
    public DateTime Date { get; set; }

    /// <summary>VEC code, e.g. "arrl".</summary>
    public string Vec { get; set; } = "";

    /// <summary>"pend" (upcoming) or "done". ExamTools has no cancelled state — cancelled sessions simply drop out of the feed.</summary>
    public string State { get; set; } = "";

    /// <summary>Registration count — lets the poller skip the applicant fetch (and its PII transfer) when nothing is registered.</summary>
    public int? ApplicantCount { get; set; }

    public ExamToolsSessionDef? SessionDef { get; set; }
}

public class ExamToolsSessionDef
{
    public string Summary { get; set; } = "";
}

/// <summary>Response of GET /api/veUser/sessions/{id}/export/basic.json.</summary>
public class ExamToolsApplicantExport
{
    public List<ExamToolsApplicant> Applicants { get; set; } = [];
}

public class ExamToolsApplicant
{
    public required string Id { get; set; }

    public string Firstname { get; set; } = "";
    public string Middle { get; set; } = "";
    public string Lastname { get; set; } = "";
    public string Suffix { get; set; } = "";

    public string Email { get; set; } = "";

    /// <summary>ExamTools uses an all-zeros placeholder when the applicant registered without an FRN.</summary>
    public string Frn { get; set; } = "";

    [JsonPropertyName("has_felony")]
    public bool? HasFelony { get; set; }

    /// <summary>Registration timestamp (UTC).</summary>
    public DateTime Created { get; set; }

    /// <summary>Combines first/middle/last/suffix into the single Name field the Candidate table stores.</summary>
    public string FullName()
    {
        string[] parts = [Firstname, Middle, Lastname, Suffix];
        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
    }

    /// <summary>True when the FRN is absent or ExamTools' all-zeros placeholder.</summary>
    public bool FrnIsMissing() => string.IsNullOrWhiteSpace(Frn) || Frn.All(c => c == '0');
}
