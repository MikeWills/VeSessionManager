using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFccIssueSuppressionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FccIssueActive",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FccIssueSuppressNewLicenseReminders",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FccIssueSuppressRenewalReminders",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FccIssueSuppressUpgradeReminders",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FccIssueActive",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "FccIssueSuppressNewLicenseReminders",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "FccIssueSuppressRenewalReminders",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "FccIssueSuppressUpgradeReminders",
                table: "SystemSettings");
        }
    }
}
