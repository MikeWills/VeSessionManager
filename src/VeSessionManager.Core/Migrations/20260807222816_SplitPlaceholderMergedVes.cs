using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Repairs databases that ran the first version of VeAsPersonWithTeamMemberships, which merged
    /// VE rows on call sign + name without asking whether the call sign was a call sign.
    ///
    /// <para>ExamTools reports the literal <c>&lt;UNKNOWN&gt;</c> when it has no call sign for a VE.
    /// Treated as an ordinary value it looks like one call sign shared by many people, so the merge
    /// fused HRCC's unidentified VE with MARC's into a single person carrying 88 sessions of both
    /// their histories. Found by running the migration against real data on 2026-08-07 — every test
    /// used realistic call signs and sailed straight past it.</para>
    ///
    /// <para><b>Unlike a general un-merge, this one is safe.</b> Splitting merged people is normally
    /// impossible because nothing records who was who. Here it is fully determined: each pre-merge
    /// row was one team's, and every session belongs to exactly one team, so each session link can
    /// be handed back to the right side without guessing.</para>
    ///
    /// <para>A no-op on any database created after the fix — the earlier migration no longer merges
    /// these rows, so there is nothing to split. Kept as a separate migration rather than folded
    /// into that one because a migration already recorded as applied never runs again, and this
    /// deployment's development database has already applied it.</para>
    /// </summary>
    public partial class SplitPlaceholderMergedVes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Memberships to detach: every one belonging to an unidentifiable person except the
            // lowest-numbered team, which keeps the original row. GLOB mirrors CallSign.IsUsable —
            // anything containing a character outside [A-Za-z0-9/] is not a call sign.
            migrationBuilder.Sql(
                """
                CREATE TEMPORARY TABLE _ve_split AS
                SELECT m.Id AS MembershipId, m.VolunteerExaminerId AS OldVeId, m.TeamId
                FROM VeTeamMemberships m
                JOIN VolunteerExaminers v ON v.Id = m.VolunteerExaminerId
                WHERE v.CallSign IS NOT NULL
                  AND v.CallSign GLOB '*[^A-Za-z0-9/]*'
                  AND m.TeamId <> (SELECT MIN(m2.TeamId) FROM VeTeamMemberships m2
                                    WHERE m2.VolunteerExaminerId = m.VolunteerExaminerId);
                """);

            migrationBuilder.Sql("ALTER TABLE _ve_split ADD COLUMN NewVeId INTEGER;");

            // One new person per detached membership. Notes carries a marker only so the new ids can
            // be correlated back below — SQLite has no INSERT ... RETURNING mapping into a join, and
            // relying on "the new rows are the highest ids" would break the moment anything else
            // inserted concurrently. Cleared again at the end.
            migrationBuilder.Sql(
                """
                INSERT INTO VolunteerExaminers (Name, CallSign, ContactPreference, LicenseNotFoundAtFcc, OperatorClass, CreatedUtc, Notes)
                SELECT v.Name, v.CallSign, v.ContactPreference, 0, 0,
                       strftime('%Y-%m-%d %H:%M:%f', 'now'), '_ve_split_' || s.MembershipId
                FROM _ve_split s
                JOIN VolunteerExaminers v ON v.Id = s.OldVeId;
                """);

            migrationBuilder.Sql(
                """
                UPDATE _ve_split
                   SET NewVeId = (SELECT v.Id FROM VolunteerExaminers v
                                   WHERE v.Notes = '_ve_split_' || _ve_split.MembershipId);
                """);

            migrationBuilder.Sql(
                """
                UPDATE VeTeamMemberships
                   SET VolunteerExaminerId = (SELECT s.NewVeId FROM _ve_split s WHERE s.MembershipId = VeTeamMemberships.Id)
                 WHERE Id IN (SELECT MembershipId FROM _ve_split WHERE NewVeId IS NOT NULL);
                """);

            // The session history follows its own team. No primary-key collision is possible: the
            // target person was created moments ago and holds no links yet.
            migrationBuilder.Sql(
                """
                UPDATE SessionVolunteerExaminers
                   SET VolunteerExaminerId = (
                        SELECT s.NewVeId FROM _ve_split s
                         JOIN Sessions ses ON ses.Id = SessionVolunteerExaminers.SessionId
                        WHERE s.OldVeId = SessionVolunteerExaminers.VolunteerExaminerId
                          AND s.TeamId = ses.TeamId
                          AND s.NewVeId IS NOT NULL)
                 WHERE EXISTS (
                        SELECT 1 FROM _ve_split s
                         JOIN Sessions ses ON ses.Id = SessionVolunteerExaminers.SessionId
                        WHERE s.OldVeId = SessionVolunteerExaminers.VolunteerExaminerId
                          AND s.TeamId = ses.TeamId
                          AND s.NewVeId IS NOT NULL);
                """);

            migrationBuilder.Sql("UPDATE VolunteerExaminers SET Notes = NULL WHERE Notes LIKE '_ve_split_%';");
            migrationBuilder.Sql("DROP TABLE _ve_split;");
        }

        /// <summary>
        /// Deliberately empty. Re-merging people this split apart would recreate the defect it
        /// exists to repair, and there is no state worth restoring — the rows it creates are the
        /// distinct people that should have existed all along.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
