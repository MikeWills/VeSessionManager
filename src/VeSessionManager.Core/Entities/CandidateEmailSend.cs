namespace VeSessionManager.Core.Entities;

/// <summary>
/// One hand-composed email that actually reached one candidate (#144) — the record behind the
/// "Email history" list on a candidate, alongside the automated sends.
///
/// <para><b>Why a table rather than another <c>...SentUtc</c> column.</b> Every automated email here
/// tracks itself with its own column on <see cref="Candidate"/>, which works because the set of
/// automated emails is fixed by what the code sends. These are not: a team writes its own templates,
/// and a column per template cannot be added by somebody at runtime. So the timestamp moves to a row
/// carrying the template's name.</para>
///
/// <para><b>Written only for a delivery that succeeded.</b> The list answers "who has already had
/// one", and a second pass over a session skips the people on it — so recording a failed send would
/// hide exactly the person that pass exists to catch.</para>
///
/// <para><b>No subject or body is stored, deliberately.</b> A subject routinely carries the
/// candidate's own name, so a store holding content is a store the PII purge has to reach into and
/// keep reaching into. The label and the timestamp answer the question that was actually asked.</para>
/// </summary>
public class CandidateEmailSend
{
    public int Id { get; set; }

    public int CandidateId { get; set; }
    public Candidate Candidate { get; set; } = null!;

    /// <summary>
    /// What the sender started from — a template's display name, or "Custom message" for a draft
    /// written from scratch. A label rather than an <c>EmailTemplate</c> foreign key on purpose: the
    /// draft is editable, so what went out is not the template, and a template deleted later must not
    /// take the history of what it sent with it.
    /// </summary>
    public required string TemplateLabel { get; set; }

    public DateTime SentUtc { get; set; }

    /// <summary>Who pressed send. Never null: nothing sends one of these without a person doing it.</summary>
    public int SentByUserId { get; set; }
    public User SentByUser { get; set; } = null!;
}
