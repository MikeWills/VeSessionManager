using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VeAsPersonWithTeamMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: the three operations EF generated here — DropForeignKey, DropIndex and a
            // RenameColumn of TeamId to OperatorClass — were replaced by hand.
            //
            // **The rename was actively dangerous.** Both columns are ints, so EF assumed one was
            // the other: every VE's team id would have been reinterpreted as their operator class,
            // silently turning "team 2" into "class 2" on every row. OperatorClass is added below as
            // a new column defaulting to None instead, and TeamId is dropped at the *end* of this
            // method — after the data migration has copied it into VeTeamMemberships, which cannot
            // happen if the column is gone by then.

            migrationBuilder.AddColumn<int>(
                name: "OperatorClass",
                table: "VolunteerExaminers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContactPreference",
                table: "VolunteerExaminers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "DiscordUsername",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LicenseCancellationDateUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LicenseExpiresUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LicenseGrantDateUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LicenseLastCheckedUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LicenseNotFoundAtFcc",
                table: "VolunteerExaminers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LicenseStatus",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VeCallSignHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolunteerExaminerId = table.Column<int>(type: "INTEGER", nullable: false),
                    CallSign = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReplacedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VeCallSignHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VeCallSignHistories_VolunteerExaminers_VolunteerExaminerId",
                        column: x => x.VolunteerExaminerId,
                        principalTable: "VolunteerExaminers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VeTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VeTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VeTags_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VeTeamMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolunteerExaminerId = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    InactivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VeTeamMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VeTeamMemberships_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VeTeamMemberships_VolunteerExaminers_VolunteerExaminerId",
                        column: x => x.VolunteerExaminerId,
                        principalTable: "VolunteerExaminers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VeVecAccreditations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolunteerExaminerId = table.Column<int>(type: "INTEGER", nullable: false),
                    VecId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccreditationNumber = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VeVecAccreditations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VeVecAccreditations_Vecs_VecId",
                        column: x => x.VecId,
                        principalTable: "Vecs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VeVecAccreditations_VolunteerExaminers_VolunteerExaminerId",
                        column: x => x.VolunteerExaminerId,
                        principalTable: "VolunteerExaminers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VeTagAssignments",
                columns: table => new
                {
                    VeTeamMembershipId = table.Column<int>(type: "INTEGER", nullable: false),
                    VeTagId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VeTagAssignments", x => new { x.VeTeamMembershipId, x.VeTagId });
                    table.ForeignKey(
                        name: "FK_VeTagAssignments_VeTags_VeTagId",
                        column: x => x.VeTagId,
                        principalTable: "VeTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VeTagAssignments_VeTeamMemberships_VeTeamMembershipId",
                        column: x => x.VeTeamMembershipId,
                        principalTable: "VeTeamMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerExaminers_CallSign",
                table: "VolunteerExaminers",
                column: "CallSign");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerExaminers_Frn",
                table: "VolunteerExaminers",
                column: "Frn",
                unique: true,
                filter: "\"Frn\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VeCallSignHistories_CallSign",
                table: "VeCallSignHistories",
                column: "CallSign");

            migrationBuilder.CreateIndex(
                name: "IX_VeCallSignHistories_VolunteerExaminerId",
                table: "VeCallSignHistories",
                column: "VolunteerExaminerId");

            migrationBuilder.CreateIndex(
                name: "IX_VeTagAssignments_VeTagId",
                table: "VeTagAssignments",
                column: "VeTagId");

            migrationBuilder.CreateIndex(
                name: "IX_VeTags_TeamId_Name",
                table: "VeTags",
                columns: new[] { "TeamId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VeTeamMemberships_TeamId",
                table: "VeTeamMemberships",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_VeTeamMemberships_VolunteerExaminerId_TeamId",
                table: "VeTeamMemberships",
                columns: new[] { "VolunteerExaminerId", "TeamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VeVecAccreditations_VecId",
                table: "VeVecAccreditations",
                column: "VecId");

            migrationBuilder.CreateIndex(
                name: "IX_VeVecAccreditations_VolunteerExaminerId_VecId",
                table: "VeVecAccreditations",
                columns: new[] { "VolunteerExaminerId", "VecId" },
                unique: true);

            // ---- Data migration: per-team VE rows become people with team memberships ------------
            //
            // Merge key is call sign AND name, not call sign alone. Two teams' "N0ABC" is virtually
            // always one person — but a call sign released and reissued to someone else is a real
            // thing, and merging two humans is not something an admin can undo once session links
            // have been repointed. Where the names disagree both rows survive, sharing a call sign
            // (the index on CallSign is deliberately non-unique for exactly this), and phase 2
            // surfaces them as possible duplicates for a human to resolve. Conservative in the
            // direction that stays recoverable.
            migrationBuilder.Sql(
                """
                CREATE TEMPORARY TABLE _ve_merge_map AS
                SELECT v.Id AS OldId,
                       (SELECT MIN(v2.Id) FROM VolunteerExaminers v2
                         WHERE v2.CallSign IS NOT NULL
                           AND v2.CallSign NOT GLOB '*[^A-Za-z0-9/]*'
                           AND UPPER(v2.CallSign) = UPPER(v.CallSign)
                           AND UPPER(TRIM(v2.Name)) = UPPER(TRIM(v.Name))) AS NewId
                FROM VolunteerExaminers v
                WHERE v.CallSign IS NOT NULL
                  AND v.CallSign NOT GLOB '*[^A-Za-z0-9/]*';
                """);

            // Rows that cannot identify a person map to themselves and are left exactly as they
            // are: no call sign at all, or a value that is not call-sign-shaped.
            //
            // **The second case is not hypothetical.** ExamTools reports the literal "<UNKNOWN>"
            // when it has no call sign for a VE, and an earlier version of this migration treated
            // that as an ordinary value — merging HRCC's unidentified VE with MARC's into one person
            // carrying 88 sessions of both their histories. Found by running against real data
            // (2026-08-07); the tests all used realistic call signs and sailed past it. The GLOB
            // excludes anything containing a character outside [A-Za-z0-9/], which catches that
            // placeholder and whatever the next one turns out to be. Mirrors CallSign.IsUsable.
            migrationBuilder.Sql(
                """
                INSERT INTO _ve_merge_map (OldId, NewId)
                SELECT Id, Id FROM VolunteerExaminers
                 WHERE CallSign IS NULL
                    OR CallSign GLOB '*[^A-Za-z0-9/]*';
                """);

            // Every pre-merge row was one team's copy of a person, so each becomes one membership.
            migrationBuilder.Sql(
                """
                INSERT INTO VeTeamMemberships (VolunteerExaminerId, TeamId, IsActive, CreatedUtc)
                SELECT DISTINCT m.NewId, v.TeamId, 1, strftime('%Y-%m-%d %H:%M:%f', 'now')
                FROM VolunteerExaminers v
                JOIN _ve_merge_map m ON m.OldId = v.Id;
                """);

            // Session links move to the survivor. Any link that would collide with one the survivor
            // already holds is deleted first — SessionVolunteerExaminers is keyed on
            // (SessionId, VolunteerExaminerId), so the UPDATE below would otherwise fail. In
            // practice this deletes nothing: a session belongs to one team, and the old unique index
            // made two rows for one call sign within a team impossible.
            migrationBuilder.Sql(
                """
                DELETE FROM SessionVolunteerExaminers
                 WHERE EXISTS (
                    SELECT 1 FROM _ve_merge_map m
                     WHERE m.OldId = SessionVolunteerExaminers.VolunteerExaminerId
                       AND m.NewId <> m.OldId
                       AND EXISTS (SELECT 1 FROM SessionVolunteerExaminers s2
                                    WHERE s2.SessionId = SessionVolunteerExaminers.SessionId
                                      AND s2.VolunteerExaminerId = m.NewId));
                """);

            migrationBuilder.Sql(
                """
                UPDATE SessionVolunteerExaminers
                   SET VolunteerExaminerId = (SELECT m.NewId FROM _ve_merge_map m
                                               WHERE m.OldId = SessionVolunteerExaminers.VolunteerExaminerId)
                 WHERE VolunteerExaminerId IN (SELECT OldId FROM _ve_merge_map WHERE NewId <> OldId);
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM VolunteerExaminers
                 WHERE Id IN (SELECT OldId FROM _ve_merge_map WHERE NewId <> OldId);
                """);

            // CreatedUtc is a new non-null column, so every surviving row carries the default
            // 0001-01-01 until this runs. The migration date is the honest answer — when this app
            // first held the person as a person. Nothing records when the original row appeared.
            migrationBuilder.Sql(
                """
                UPDATE VolunteerExaminers
                   SET CreatedUtc = strftime('%Y-%m-%d %H:%M:%f', 'now')
                 WHERE CreatedUtc IS NULL OR CreatedUtc < '1900-01-01';
                """);

            migrationBuilder.Sql("DROP TABLE _ve_merge_map;");

            // Only now is TeamId expendable.
            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerExaminers_Teams_TeamId",
                table: "VolunteerExaminers");

            migrationBuilder.DropIndex(
                name: "IX_VolunteerExaminers_TeamId_CallSign",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "VolunteerExaminers");
        }

        /// <inheritdoc />
        /// <summary>
        /// <b>Restores the schema, not the data.</b> The merge in <c>Up</c> is one-way: two per-team
        /// rows that became one person cannot be split back into two, because nothing records which
        /// half of the merged contact details, tags or accreditations belonged to which team — and
        /// the session links they shared have been repointed. A VE who serves several teams keeps
        /// whichever team id comes out of the backfill below and loses the rest.
        ///
        /// <para>So this exists to make the schema reversible, which is what a failed deploy needs.
        /// Recovering the <i>data</i> means restoring the pre-migration backup, per CLAUDE.md's
        /// rollback rule. Say so out loud rather than letting a green down-migration imply
        /// otherwise.</para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Added and backfilled first: VeTeamMemberships is the only place the team association
            // now lives, and the DropTable calls below are about to remove it.
            migrationBuilder.AddColumn<int>(
                name: "TeamId",
                table: "VolunteerExaminers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE VolunteerExaminers
                   SET TeamId = COALESCE((SELECT MIN(m.TeamId) FROM VeTeamMemberships m
                                           WHERE m.VolunteerExaminerId = VolunteerExaminers.Id), 0);
                """);

            // A VE with no membership at all would carry TeamId 0, which the restored foreign key
            // below would reject. There is no sensible team to invent for them, so they go.
            migrationBuilder.Sql("DELETE FROM SessionVolunteerExaminers WHERE VolunteerExaminerId IN (SELECT Id FROM VolunteerExaminers WHERE TeamId = 0);");
            migrationBuilder.Sql("DELETE FROM VolunteerExaminers WHERE TeamId = 0;");

            migrationBuilder.DropTable(
                name: "VeCallSignHistories");

            migrationBuilder.DropTable(
                name: "VeTagAssignments");

            migrationBuilder.DropTable(
                name: "VeVecAccreditations");

            migrationBuilder.DropTable(
                name: "VeTags");

            migrationBuilder.DropTable(
                name: "VeTeamMemberships");

            migrationBuilder.DropIndex(
                name: "IX_VolunteerExaminers_CallSign",
                table: "VolunteerExaminers");

            migrationBuilder.DropIndex(
                name: "IX_VolunteerExaminers_Frn",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "AddressLine1",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "City",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "ContactPreference",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "CreatedUtc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "DiscordUsername",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "LicenseCancellationDateUtc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "LicenseExpiresUtc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "LicenseGrantDateUtc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "LicenseLastCheckedUtc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "LicenseNotFoundAtFcc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "LicenseStatus",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "State",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "UpdatedUtc",
                table: "VolunteerExaminers");

            // NOT a rename back to TeamId, which is what EF generated: OperatorClass holds a
            // license class by now, and renaming it would hand every VE a team id taken from their
            // license. TeamId was added and backfilled at the top of this method instead.
            migrationBuilder.DropColumn(
                name: "OperatorClass",
                table: "VolunteerExaminers");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerExaminers_TeamId_CallSign",
                table: "VolunteerExaminers",
                columns: new[] { "TeamId", "CallSign" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerExaminers_Teams_TeamId",
                table: "VolunteerExaminers",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
