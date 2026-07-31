using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Replaces the FCC bulk-file watcher's settings with the ULS lookup watcher's (2026-07-31).
    ///
    /// <para>**Hand-written, deliberately — do not regenerate.** EF's scaffolder paired the columns
    /// by position and produced a silently destructive mapping: it renamed
    /// FccWeeklyCatchupIntervalHours (24) into UlsWatcherStartHourEt, yielding an out-of-range
    /// hour 24, and FccWeeklyCatchupDayOfWeek (Monday = 1) into UlsWatcherIntervalHours, turning a
    /// twice-daily check into an hourly one. The daily-watcher pair is the semantically equivalent
    /// one, so renaming *those* carries an admin's configured cadence across untouched (12/8 stays
    /// 12/8) and the weekly pair — which has no counterpart now that there are no files to catch up
    /// on — is simply dropped.</para>
    /// </summary>
    public partial class UlsWatcherReplacesFccFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Same meaning, new name: "how often" and "first check of the day, ET".
            migrationBuilder.RenameColumn(
                name: "FccDailyWatcherIntervalHours",
                table: "SystemSettings",
                newName: "UlsWatcherIntervalHours");

            migrationBuilder.RenameColumn(
                name: "FccDailyWatcherStartHourEt",
                table: "SystemSettings",
                newName: "UlsWatcherStartHourEt");

            // No counterpart: the weekly catch-up existed only because an FCC day-name file was a
            // one-shot window that could be missed permanently. A ULS lookup returns current state
            // on every call, so there is nothing to catch up on.
            migrationBuilder.DropColumn(
                name: "FccWeeklyCatchupIntervalHours",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "FccWeeklyCatchupDayOfWeek",
                table: "SystemSettings");

            migrationBuilder.AddColumn<string>(
                name: "UlsApplicationFileNumber",
                table: "Candidates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UlsApplicationFileNumber",
                table: "Candidates");

            migrationBuilder.RenameColumn(
                name: "UlsWatcherIntervalHours",
                table: "SystemSettings",
                newName: "FccDailyWatcherIntervalHours");

            migrationBuilder.RenameColumn(
                name: "UlsWatcherStartHourEt",
                table: "SystemSettings",
                newName: "FccDailyWatcherStartHourEt");

            // Restored with the values SystemSettingsService used to seed, so a rolled-back
            // deployment comes up on its previous schedule rather than a zeroed one.
            migrationBuilder.AddColumn<int>(
                name: "FccWeeklyCatchupIntervalHours",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "FccWeeklyCatchupDayOfWeek",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }
    }
}
