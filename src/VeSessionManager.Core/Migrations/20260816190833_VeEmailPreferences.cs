using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VeEmailPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailUnsubscribedUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnsubscribeToken",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailSubscribed",
                table: "VeTeamMemberships",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "VeEmailSubscriptionsEnabled",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailUnsubscribedUtc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "UnsubscribeToken",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "EmailSubscribed",
                table: "VeTeamMemberships");

            migrationBuilder.DropColumn(
                name: "VeEmailSubscriptionsEnabled",
                table: "Teams");
        }
    }
}
