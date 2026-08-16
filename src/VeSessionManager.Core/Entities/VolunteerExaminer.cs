namespace VeSessionManager.Core.Entities;

/// <summary>
/// A volunteer examiner — <b>a person, not a team's copy of a person</b> (issue #142, 2026-08-07).
///
/// <para>Until now this row carried a <c>TeamId</c> and was unique on <c>(TeamId, CallSign)</c>, so
/// one human serving two teams existed twice with nothing linking the two. Everything issue #142
/// asks for — contact details, tags, VEC accreditations, self-service — is a fact about the person,
/// so the row became the person and <see cref="VeTeamMembership"/> carries the per-team part. Same
/// shape as the earlier <c>User.TeamId</c> -> <c>UserTeams</c> change (issues #17/#19).</para>
///
/// <para><b>Identity is <see cref="Id"/>, and after that <see cref="Frn"/> — never the call sign.</b>
/// A call sign changes (vanity); the person does not. Every relationship in the app points at
/// <c>Id</c>, so a rename is invisible to session history, memberships and accreditations. FCC's
/// registration number survives a rename and is the stable *external* key, which is why the ULS
/// sweep backfilling it (issue #107) is what ultimately makes matching robust —
/// <see cref="CallSign"/> is a current attribute that gets overwritten, with the previous value kept
/// in <see cref="VeCallSignHistory"/> so a stale ExamTools roster still resolves to the right
/// person.</para>
///
/// <para><b>ExamTools owns membership and nothing else.</b> It supplies call sign and name on a
/// session roster and has no contact information at all. <see cref="Name"/> is seeded from it the
/// first time a VE is seen and is app-owned from then on; every field below it is only ever written
/// by an admin or by the VE themselves. Before this, the sync service overwrote Name on every poll
/// — harmless while nothing could edit it, and a guaranteed clobber-every-hour bug the moment
/// something could.</para>
/// </summary>
public class VolunteerExaminer : ILicenseSnapshot
{
    public int Id { get; set; }

    /// <summary>Seeded from ExamTools on first sight, app-owned thereafter — the sync never overwrites it again.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Current call sign, always stored upper-invariant. <b>An attribute, not a key</b> — unique
    /// among VEs at any one moment (nobody holds someone else's call), but mutable: when FCC reports
    /// a different call sign for a known FRN the value is replaced and the old one is written to
    /// <see cref="CallSignHistory"/>.
    /// <para>Nullable because ExamTools' roster can name a VE without one, which leaves the license
    /// check with nothing to look up — a state that has to be shown rather than read as "fine".</para>
    /// </summary>
    public string? CallSign { get; set; }

    /// <summary>
    /// FCC Registration Number — the stable external identity, unaffected by a call sign change.
    /// <para>Null for now on almost every row: ExamTools' VE roster does not report it. It is
    /// backfilled by the ULS sweep (issue #107), which looks up by call sign and gets the FRN in the
    /// response — the same trick <c>LicenseWatchService</c> already uses to give a call-sign-entered
    /// watch row its FRN.</para>
    /// </summary>
    public string? Frn { get; set; }

    // ---- Contact details (issue #142) -----------------------------------------------------------
    // On the person, deliberately shared across every team they serve: this deployment hosts
    // cooperating teams rather than unrelated organizations, so three teams holding three divergent
    // addresses for one person would be worse than one shared record.
    //
    // **Visible to TeamAdmin/SystemAdmin and to the VE themselves. Nobody else — not a Session
    // Manager, not a Team Lead.** And unlike call sign, FRN and license class, NONE of this is
    // public FCC record data. The address here is the VE's *home* address, given to their team in
    // confidence; the address on the public FCC/QRZ record is typically a PO box precisely because
    // they chose not to publish where they live. Treating the two as interchangeable is the mistake
    // this comment exists to prevent — including for the QRZ prefill (issue #142), which can only
    // ever return the public one and must therefore never overwrite a hand-entered value, and for
    // any export, which carries real home addresses out of the database in bulk.

    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>
    /// When this VE asked to stop receiving email from the app, or null if they have not (#191).
    /// Set by them, from a link in the message itself — never by an admin, which is the point of an
    /// unsubscribe.
    ///
    /// <para><b>It stops every email this app sends them, not only bulk messages.</b> A partly-honoured
    /// unsubscribe is worse than none: somebody who clicks it has said stop, and continuing to send
    /// session invitations because those are arguably "transactional" is the reading that gets people
    /// marked as spam. The operational cost is real and deliberate — an unsubscribed VE has to be
    /// telephoned about a session — so both the directory and the invitation screen show the state
    /// rather than silently dropping them.</para>
    ///
    /// <para>The two account-flow emails a VE triggers themselves (a self-service sign-in link, an
    /// email-change confirmation) are unaffected: they are replies to an action taken seconds earlier,
    /// and suppressing them would break the only route a VE has back to their own details.</para>
    /// </summary>
    public DateTime? EmailUnsubscribedUtc { get; set; }

    /// <summary>
    /// This VE's unsubscribe token, minted the first time one is needed and then stable for the life
    /// of the record.
    ///
    /// <para><b>Deliberately not a <see cref="VeSelfServiceToken"/>.</b> Those are single-use and
    /// short-lived, which is right for something that authenticates. An unsubscribe link is the
    /// opposite: it has to work whenever the recipient gets round to it — CAN-SPAM requires the
    /// mechanism to keep working for at least 30 days after the message, and in practice people click
    /// one in a months-old email — and clicking it twice must not fail.</para>
    ///
    /// <para><b>Stored in the clear, which is a deliberate exception to the hash-at-rest convention
    /// every other token here follows.</b> That convention protects tokens that <i>authenticate</i>:
    /// a leaked one reaches a person's contact details or confirms a change of address. Only a hash
    /// can be stored for those because they are short-lived and re-issued on demand. This one is
    /// neither — it must stay valid indefinitely and cannot be re-derived from a digest, so a stored
    /// hash would force re-minting on every send and break the link in every message already
    /// delivered. What it grants is correspondingly tiny: stop, or resume, email to this one person.
    /// It exposes no name, address or history, and anyone holding a leaked database already has the
    /// email address it would be used against.</para>
    /// </summary>
    public string? UnsubscribeToken { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }

    /// <summary>Ties the roster to the team's Discord server — "completing the loop" in issue #142. Free text; not validated against Discord.</summary>
    public string? DiscordUsername { get; set; }

    public VeContactPreference ContactPreference { get; set; } = VeContactPreference.Email;

    /// <summary>Admin-facing free text. Not shown to the VE.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>
    /// When this VE's contact details were cleared by the retention purge (#313 / L-07), or null if
    /// they never have been. Both the "needs purging" query filter and the idempotency guard, the
    /// same idiom as <see cref="Candidate.PiiPurgedUtc"/> and every other scan-based job here.
    ///
    /// <para>A purged VE is not a deleted one: name, call sign, FRN, accreditations and session
    /// history all remain, because they are the accreditation trail. See
    /// <see cref="VolunteerExaminerPiiFields"/> for the split and docs/ve-retention.md for the
    /// policy.</para>
    ///
    /// <para>Cleared again if they come back — a returning VE re-enters their details through the
    /// normal edit path, and this field is set back to null there so the record stops looking
    /// purged.</para>
    /// </summary>
    public DateTime? PiiPurgedUtc { get; set; }

    // ---- Cached FCC license state (issue #107) --------------------------------------------------
    // Added with the rest of the columns rather than in a second migration, since this table is
    // being rewritten anyway. Populated by the ULS sweep in phase 3; every field is null until then.
    //
    // Columns here rather than auto-created WatchedLicense rows: the Renewal Monitor's whole premise
    // is a list a human curated, and filling it with thirty VEs nobody added would break that.

    public DateTime? LicenseLastCheckedUtc { get; set; }

    /// <summary>True when the last lookup answered "notfound" — distinguishable from "not looked up yet" (null <see cref="LicenseLastCheckedUtc"/>) and from "no call sign to look up".</summary>
    public bool LicenseNotFoundAtFcc { get; set; }

    public string? LicenseStatus { get; set; }
    public LicenseClass OperatorClass { get; set; }
    public DateTime? LicenseGrantDateUtc { get; set; }

    /// <summary>End of the current term. The session-relative question — "will this VE be expired on Saturday?" — is derived from this against the session date, never stored.</summary>
    public DateTime? LicenseExpiresUtc { get; set; }

    public DateTime? LicenseCancellationDateUtc { get; set; }

    /// <summary>
    /// The FRN FCC returned for this VE that could not be stored, because another record already
    /// holds it. Unique per person, so a value here is <b>proof</b> that this record and that one
    /// are the same human.
    ///
    /// <para>Exists because the proof was otherwise ephemeral: the sweep detected the collision,
    /// wrote a warning to the log and moved on, so the merge screen — the one place the evidence
    /// matters — could only see a shared call sign and had to say "needs checking" about something
    /// already established. Not indexed and deliberately not unique: it is a note about a conflict,
    /// not an identifier.</para>
    ///
    /// <para>Cleared when the conflict resolves, either because the merge happened or because the
    /// other record released the FRN.</para>
    /// </summary>
    public string? ConflictingFrn { get; set; }

    /// <summary>
    /// Set when this record was merged into another because they turned out to be the same person —
    /// proved by both resolving to one FRN, which is unique per person.
    ///
    /// <para><b>The row is kept, never deleted.</b> A hard delete after repointing would leave no
    /// trace that the duplicate ever existed and no path back. A global query filter in AppDbContext
    /// hides merged rows from every query at once, which matters more than it sounds: the
    /// alternative is an invariant that every future query has to remember, and one eventually
    /// will not.</para>
    ///
    /// <para>This records <i>that</i> a merge happened. Which session links came from which side is
    /// recorded in the audit entry — without that, an un-merge could not tell whose history was
    /// whose, and calling the merge reversible would be an overclaim.</para>
    /// </summary>
    public int? MergedIntoVolunteerExaminerId { get; set; }
    public VolunteerExaminer? MergedIntoVolunteerExaminer { get; set; }

    // ---- Relationships --------------------------------------------------------------------------

    public List<VeTeamMembership> TeamMemberships { get; } = [];
    public List<VeVecAccreditation> VecAccreditations { get; } = [];
    public List<VeCallSignHistory> CallSignHistory { get; } = [];
    public List<SessionVolunteerExaminer> SessionVolunteerExaminers { get; } = [];
}

/// <summary>
/// How a VE wants to be contacted about upcoming sessions. Text is deliberately present but
/// unselectable in the UI until SMS actually exists (issue #142) — modelling it now avoids a
/// migration later, and a stored value nothing can set is harmless.
/// </summary>
public enum VeContactPreference
{
    Email = 0,
    Text = 1,
    Both = 2
}
