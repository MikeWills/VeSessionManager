using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Declares four column defaults the model was missing (#314, L-18).
    ///
    /// <para><b>This changes no data and, on an already-migrated database, no effective schema.</b>
    /// Each of these columns was created by an earlier AddColumn that already specified the default;
    /// what was missing was the matching HasDefaultValue on the model, so a schema built FROM the
    /// model (EnsureCreated — the SQLite tests) lacked it while the migrated one had it. A row
    /// inserted outside EF therefore hit a NOT NULL failure on one and succeeded on the other.</para>
    ///
    /// <para><b>Note the cost:</b> SQLite has no ALTER COLUMN, so EF implements each of these as a
    /// table rebuild — create, copy, drop, rename — inside the migration transaction. Candidates and
    /// AspNetUsers are both rebuilt. Correct and one-time, but not free on a large Candidates table,
    /// which is why it is called out here rather than discovered from a slow deploy.</para>
    /// </summary>
    public partial class DeclareColumnDefaultsOnModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "FccPaymentStatus",
                table: "Candidates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "FccHoldReason",
                table: "Candidates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "ThemePreference",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<bool>(
                name: "MustChangePassword",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "FccPaymentStatus",
                table: "Candidates",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "FccHoldReason",
                table: "Candidates",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "ThemePreference",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<bool>(
                name: "MustChangePassword",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: false);
        }
    }
}
