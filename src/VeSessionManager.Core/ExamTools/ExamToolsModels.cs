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

    /// <summary>Seconds. Present on both the list and detail endpoints.</summary>
    public int Duration { get; set; }

    /// <summary>ExamTools' own short lead-VE-callsign code (e.g. "KM6Z - W5CBW" or "AD2GX") — the
    /// parenthetical text ExamTools' own calendar UI shows next to the team name, verified live
    /// 2026-07-30 byte-for-byte against real HRCC sessions. Already present on the cheap team-list
    /// endpoint (`GET /api/veUser/sessions?team=...`), not just the per-session detail one — no
    /// extra API call needed to get it.</summary>
    public string? ExtId { get; set; }
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

/// <summary>
/// Response of GET /api/veUser/sessions/{id}/export/full.json. Wrapped under a DEVDOC key on the dev
/// site (examtools.dev) — but confirmed live 2026-07-29 against real HRCC/prod (alpha.exam.tools)
/// data that prod does NOT wrap it at all; VEs/applicants sit at the top level instead. This is
/// exactly the "wrapper key may differ on prod, re-verify" risk docs/examtools-api.md already
/// flagged as unverified — it turned out to differ more than expected (no wrapper, not just a
/// different key name), and silently meant VolunteerExaminerSyncService found zero VEs for every
/// real HRCC session the whole time (issue #38). Both shapes are mapped here; Ves() picks whichever
/// is actually populated, dev-wrapped taking priority only because it's checked first — a payload
/// only ever has one or the other, never both.
/// </summary>
public class ExamToolsFullExport
{
    public ExamToolsFullExportDevDoc? Devdoc { get; set; }

    /// <summary>Prod's shape (alpha.exam.tools) — top-level, not wrapped under "devdoc".</summary>
    public List<ExamToolsVe>? Ves { get; set; }

    /// <summary>Picks whichever shape this payload actually used.</summary>
    public List<ExamToolsVe> ResolveVes() => Devdoc?.Ves ?? Ves ?? [];
}

public class ExamToolsFullExportDevDoc
{
    public List<ExamToolsVe> Ves { get; set; } = [];
}

/// <summary>One VE credited on a session's full export (DEVDOC.VEs) — the team lead plus every co-VE who signed off.</summary>
public class ExamToolsVe
{
    public string Call { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Response of GET /api/veUser/sessions/{sessionId}/applicant/{applicantId} — full applicant detail
/// including graded exam results, verified against real HRCC data 2026-07-28. Only the fields
/// ExamResultSyncService needs are mapped (this endpoint also returns full registration PII already
/// covered by ExamToolsApplicant — address/phone/etc. deliberately left unmapped here since nothing
/// needs them).
/// </summary>
public class ExamToolsApplicantDetail
{
    public List<ExamToolsExamResult> Exams { get; set; } = [];
}

/// <summary>One exam element attempt. A candidate can have more than one entry in the same sitting (e.g. passes Technician, then attempts and fails General) — ExamResultSyncService treats any graded-and-failed entry as an overall Failed, regardless of other elements passed the same session.</summary>
public class ExamToolsExamResult
{
    public int Element { get; set; }

    /// <summary>False while the exam is still in progress/ungraded — Exams entries with Graded=false are ignored, not treated as failed.</summary>
    public bool Graded { get; set; }

    public bool Passed { get; set; }
}
