namespace VeSessionManager.Core.Entities;

/// <summary>
/// Single definition of which fields count as a <see cref="VolunteerExaminer"/>'s personal data, and
/// how they are cleared — the same shape as <see cref="CandidatePiiFields"/> and for the same
/// reason: two definitions of "PII cleared" drift, and the drift is silent.
///
/// <para><b>What is deliberately NOT cleared, and why.</b> Name, call sign, FRN, license class,
/// accreditations and session history all stay. Those are the accreditation trail — the record that
/// this person was qualified to administer the exams they administered — and a VEC may need it long
/// after someone stops volunteering. Call sign and FRN are also public FCC record data, the same
/// ruling already applied to candidates.</para>
///
/// <para><b>What is cleared is the part that was given in confidence.</b> The address here is the
/// VE's <i>home</i> address, handed to their team privately; the address on the public FCC/QRZ
/// record is typically a PO box precisely because they chose not to publish where they live. See
/// <see cref="VolunteerExaminer"/>'s own remarks — that distinction is the whole reason this helper
/// exists rather than "delete the row".</para>
///
/// <para>Notes is cleared too. It is admin-facing free text that no rule constrains, so it is the
/// one field that may contain anything at all about a person — which makes keeping it after their
/// contact details have aged out indefensible.</para>
/// </summary>
public static class VolunteerExaminerPiiFields
{
    public static void Clear(VolunteerExaminer volunteerExaminer, DateTime purgedUtc)
    {
        volunteerExaminer.Email = null;
        volunteerExaminer.Phone = null;
        volunteerExaminer.AddressLine1 = null;
        volunteerExaminer.AddressLine2 = null;
        volunteerExaminer.City = null;
        volunteerExaminer.State = null;
        volunteerExaminer.PostalCode = null;
        volunteerExaminer.DiscordUsername = null;

        // The same fact as the username above, and the stronger form of it: a snowflake never changes,
        // so keeping it while clearing the label would leave a permanent handle on the person's Discord
        // account after their contact details were supposed to have aged out (#519). The cost is real
        // and accepted — a purged VE who is still in the team's server has to be matched by call sign
        // again on the next check, exactly as they were the first time.
        volunteerExaminer.DiscordUserId = null;
        volunteerExaminer.Notes = null;
        volunteerExaminer.PiiPurgedUtc = purgedUtc;
    }
}
