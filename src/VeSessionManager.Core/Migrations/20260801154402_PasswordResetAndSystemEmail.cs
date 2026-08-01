using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class PasswordResetAndSystemEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemSmtpFromAddress",
                table: "SystemSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemSmtpFromDisplayName",
                table: "SystemSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemSmtpHost",
                table: "SystemSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemSmtpPassword",
                table: "SystemSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SystemSmtpPort",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SystemSmtpUseStartTls",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemSmtpUsername",
                table: "SystemSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPasswordResetRequestedUtc",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SystemSmtpFromAddress",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "SystemSmtpFromDisplayName",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "SystemSmtpHost",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "SystemSmtpPassword",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "SystemSmtpPort",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "SystemSmtpUseStartTls",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "SystemSmtpUsername",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "LastPasswordResetRequestedUtc",
                table: "AspNetUsers");
        }
    }
}
