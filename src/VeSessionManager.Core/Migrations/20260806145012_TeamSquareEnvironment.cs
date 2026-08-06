using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class TeamSquareEnvironment : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// <b>Every existing team lands on Sandbox (0), including teams that were live on Production.</b>
        /// The migration cannot know better: the old setting was deployment config
        /// (<c>Square:Environment</c> in appsettings), not data, so there is nothing in the database
        /// to read the previous value from.
        ///
        /// <para><b>Post-deploy step: set each live team back to Production in Team Settings.</b>
        /// Until that is done, a live team's payment links will not be created — but they fail
        /// <i>safely</i> and visibly: a Production access token is rejected by the Sandbox host, so
        /// the run surfaces as failed link generation in Job History rather than as real money moving
        /// through the wrong account. Sandbox is the default for exactly that reason; the reverse
        /// default would make a misconfiguration invisible and billable.</para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SquareEnvironment",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SquareEnvironment",
                table: "Teams");
        }
    }
}
