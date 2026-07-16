using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobRunHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobName = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRunHistories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    ManagedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Users_ManagedByUserId",
                        column: x => x.ManagedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Vecs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SupportsYouthProgram = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vecs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerExaminers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CallSign = table.Column<string>(type: "TEXT", nullable: true),
                    Frn = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerExaminers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<int>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailTemplates_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FeeConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VecId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FeeCollectionEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExamFeeAmount = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                    RetainedAmount = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeeConfigurations_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeeConfigurations_Vecs_VecId",
                        column: x => x.VecId,
                        principalTable: "Vecs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExamToolsSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduledStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ZoomMeetingId = table.Column<string>(type: "TEXT", nullable: true),
                    ZoomJoinUrl = table.Column<string>(type: "TEXT", nullable: true),
                    DiscordEventId = table.Column<string>(type: "TEXT", nullable: true),
                    VecId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeeConfigurationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CancelledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RescheduleFlaggedForReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    RescheduleFlaggedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TestingCompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TestingCompletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ArrlSubmissionStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ArrlSubmittedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArrlSubmittedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_FeeConfigurations_FeeConfigurationId",
                        column: x => x.FeeConfigurationId,
                        principalTable: "FeeConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sessions_Users_ArrlSubmittedByUserId",
                        column: x => x.ArrlSubmittedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sessions_Users_TestingCompletedByUserId",
                        column: x => x.TestingCompletedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sessions_Vecs_VecId",
                        column: x => x.VecId,
                        principalTable: "Vecs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    Frn = table.Column<string>(type: "TEXT", nullable: true),
                    FrnMissingAtRegistration = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasFelonyDisclosure = table.Column<bool>(type: "INTEGER", nullable: true),
                    DateRegisteredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ApplicationStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    Tested = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApplicationDateEnteredUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CallSign = table.Column<string>(type: "TEXT", nullable: true),
                    LicenseGrantDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResultMarkedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResultMarkedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PiiPurgedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Candidates_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Candidates_Users_ResultMarkedByUserId",
                        column: x => x.ResultMarkedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SessionVolunteerExaminers",
                columns: table => new
                {
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    VolunteerExaminerId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionVolunteerExaminers", x => new { x.SessionId, x.VolunteerExaminerId });
                    table.ForeignKey(
                        name: "FK_SessionVolunteerExaminers_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SessionVolunteerExaminers_VolunteerExaminers_VolunteerExaminerId",
                        column: x => x.VolunteerExaminerId,
                        principalTable: "VolunteerExaminers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CandidateId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentLinkUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SquarePaymentReferenceId = table.Column<string>(type: "TEXT", nullable: true),
                    PaidDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiredUnpaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    PaymentReminderSentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefundRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    RefundRequestedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RefundRequestedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefundNotes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Candidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "Candidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Users_RefundRequestedByUserId",
                        column: x => x.RefundRequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserId",
                table: "AuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_ResultMarkedByUserId",
                table: "Candidates",
                column: "ResultMarkedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SessionId",
                table: "Candidates",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_Key",
                table: "EmailTemplates",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_UpdatedByUserId",
                table: "EmailTemplates",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FeeConfigurations_CreatedByUserId",
                table: "FeeConfigurations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FeeConfigurations_VecId",
                table: "FeeConfigurations",
                column: "VecId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CandidateId",
                table: "Payments",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RefundRequestedByUserId",
                table: "Payments",
                column: "RefundRequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ArrlSubmittedByUserId",
                table: "Sessions",
                column: "ArrlSubmittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_FeeConfigurationId",
                table: "Sessions",
                column: "FeeConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TestingCompletedByUserId",
                table: "Sessions",
                column: "TestingCompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_VecId",
                table: "Sessions",
                column: "VecId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionVolunteerExaminers_VolunteerExaminerId",
                table: "SessionVolunteerExaminers",
                column: "VolunteerExaminerId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ManagedByUserId",
                table: "Users",
                column: "ManagedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "EmailTemplates");

            migrationBuilder.DropTable(
                name: "JobRunHistories");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "SessionVolunteerExaminers");

            migrationBuilder.DropTable(
                name: "Candidates");

            migrationBuilder.DropTable(
                name: "VolunteerExaminers");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "FeeConfigurations");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Vecs");
        }
    }
}
