using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Drops AccreditationNumber and ExpiresUtc — an accreditation is presence-only now
    /// (Mike, 2026-08-09). Keeping one current is the VE's own responsibility, and this app can
    /// verify neither field; a stored expiry nobody refreshes is worse than none, because the
    /// screens present it as fact and would refuse people over a date typed once.
    ///
    /// <para><b>This deliberately loses data</b> — EF's own scaffolder warns about it, and the
    /// warning is correct. Any numbers or expiry dates already entered go. <c>Down</c> restores the
    /// columns but not their contents, which is the honest position: the schema is reversible, the
    /// data is not. See CLAUDE.md's rollback rule.</para>
    /// </summary>
    public partial class VeAccreditationPresenceOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccreditationNumber",
                table: "VeVecAccreditations");

            migrationBuilder.DropColumn(
                name: "ExpiresUtc",
                table: "VeVecAccreditations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccreditationNumber",
                table: "VeVecAccreditations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresUtc",
                table: "VeVecAccreditations",
                type: "TEXT",
                nullable: true);
        }
    }
}
