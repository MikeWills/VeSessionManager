namespace VeSessionManager.Core.Entities;

/// <summary>
/// Not in the original shared data model — added as a multi-team foundation. A Team is the group
/// of VEs operating a deployment of this app (holds Discord/Zoom/ExamTools/Square credentials);
/// a Vec is the FCC-recognized coordinating org (ARRL, W5YI, etc.) a team's sessions are run
/// under. The hierarchy is VEC ⇒ Team ⇒ VE, not the reverse — Vec is deliberately NOT owned by
/// Team here (see docs/multi-team.md): it stays a shared/global reference table, since a VEC
/// dictates fees universally, not per-team, and the same VEC can be shared by multiple teams. A
/// Session references both independently (VecId for its fee schedule, TeamId for which team ran
/// it) with no relationship required between Vec and Team themselves.
/// </summary>
public class Team
{
    public int Id { get; set; }
    public required string Name { get; set; }

    // ExamTools credentials — nullable, same "graceful skip until configured" pattern as every
    // other integration's IsConfigured gate in this codebase (Zoom/Discord/Square/Email), just
    // living on the entity now instead of a client, since credentials are per-Team, not global.
    /// <summary>The sessions API's "?team=" filter value, e.g. WX0MIK.</summary>
    public string? ExamToolsTeamCode { get; set; }
    public string? ExamToolsUsername { get; set; }
    public string? ExamToolsPassword { get; set; }
    /// <summary>Per-team override of the global ExamToolsOptions.BaseUrl appsettings default (e.g. a dev team running against https://examtools.dev while others run against https://alpha.exam.tools from the same deployment) — null means "use the global default." See docs/examtools-api.md.</summary>
    public string? ExamToolsBaseUrl { get; set; }

    // Zoom credentials — nullable. Unlike ExamTools/Square/Email, this team's own separate Zoom
    // subscription/S2S OAuth app (confirmed with the user — not shared across teams).
    public string? ZoomAccountId { get; set; }
    public string? ZoomClientId { get; set; }
    public string? ZoomClientSecret { get; set; }
    /// <summary>Which Zoom user's calendar meetings get created under — defaults to "me" in code (ZoomClient) when null, not required to be set explicitly.</summary>
    public string? ZoomUserId { get; set; }

    /// <summary>
    /// How many empty "Exam Room N" Zoom breakout rooms to pre-create on every session's meeting —
    /// a genuine per-team business setting, not a credential, so unlike the Zoom fields above it
    /// stores a real default instead of null-means-unset (same reasoning as PurgeUnpaidLinkDays
    /// below). 0 means no breakout rooms are requested. There's no data this app tracks today
    /// (parallel testing "stations," not just individual VEs) that would let a count be inferred
    /// automatically, so this stays an explicit admin setting rather than computed — see
    /// docs/zoom-discord-scheduling.md's "Breakout rooms" section.
    /// </summary>
    public int ZoomBreakoutRoomCount { get; set; } = 2;

    /// <summary>Which Discord server this team's events post to — the bot itself is shared globally (Discord:BotToken, confirmed with the user), only the Guild varies per team. Null means this team hasn't picked one yet.</summary>
    public ulong? DiscordGuildId { get; set; }

    // Square credentials — nullable. This team's own separate Square merchant account (confirmed
    // with the user — not shared across teams), including which API environment those credentials are
    // for: a token authenticates against one host only, so it travels with them (2026-08-06).
    /// <summary>Which Square API this team's credentials are for. Sandbox by default — a token is issued for one environment and fails against the other, so this belongs with the credentials rather than in deployment config. See SquareApiEnvironment.</summary>
    public SquareApiEnvironment SquareEnvironment { get; set; } = SquareApiEnvironment.Sandbox;

    public string? SquareAccessToken { get; set; }
    public string? SquareLocationId { get; set; }
    public string? SquareWebhookSignatureKey { get; set; }
    /// <summary>Must exactly match this team's webhook subscription's notification URL configured in the Square Developer portal — required input to signature verification, not just where Square happens to POST. Should be https://&lt;host&gt;/webhooks/square/{this team's Id}.</summary>
    public string? SquareWebhookNotificationUrl { get; set; }

    // SMTP credentials — nullable, no defaults baked in (a shipped default like "smtp.mailgun.org"
    // would make IsEmailConfigured read true before an admin actually finished setup — see the
    // CLAUDE.md gotcha this already caused once). This team's own separate SMTP account (confirmed
    // with the user — not shared across teams). SmtpPort/SmtpUseStartTls fall back to 587/true in
    // code (CandidateNotificationService/PaymentReminderService) when null, not stored as a default.
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool? SmtpUseStartTls { get; set; }

    /// <summary>
    /// This team's logo, embedded in outgoing email wherever a template uses <c>{{Logo}}</c>.
    /// Null when no logo has been uploaded, in which case <c>{{Logo}}</c> renders to nothing and
    /// templates carrying it stay perfectly valid.
    ///
    /// <para><b>Stored in the database rather than on disk, deliberately.</b> `deploy.yml` runs
    /// `rsync --delete` over the app directory on every release — that is precisely why the SQLite
    /// file lives outside it — so an uploads folder under wwwroot would be wiped by the next deploy.
    /// As a column it is per-team data sitting beside the other per-team settings, and it is backed
    /// up by whatever backs up the database.</para>
    ///
    /// <para>Not encrypted, unlike the credential columns above: a logo is public branding that ends
    /// up in every candidate's inbox, so <see cref="Data.EncryptedStringConverter"/> would add cost
    /// and key-ring risk protecting something that is published by design.</para>
    /// </summary>
    public byte[]? LogoBytes { get; set; }

    /// <summary>MIME type of <see cref="LogoBytes"/> — only <c>image/png</c> and <c>image/jpeg</c> are accepted on upload. Needed at send time to label the MIME part correctly.</summary>
    public string? LogoContentType { get; set; }

    public DateTime? LogoUpdatedUtc { get; set; }

    /// <summary>How many days an Unpaid Payment's Square link stays live before SquarePaymentLinkPurgeService
    /// deletes it — a genuine per-team business setting, not a credential, so unlike every field
    /// above it stores a real default instead of null-means-unset. See docs/payment-link-purge.md.</summary>
    public int PurgeUnpaidLinkDays { get; set; } = 30;

    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Tracks when SessionIngestionJob's per-team pipeline last actually ran for this team, so it
    /// can throttle to SystemSettings.SessionIngestionIntervalMinutes. See IngestionScheduleService.
    ///
    /// Still not an editable setting, but no longer invisible: it is now *displayed* (never written)
    /// on Admin → Team Maintenance as "last poll / next poll", and the newest value across all teams
    /// is what the Worker-health banner tests against — see IngestionStatusService. Note that the
    /// team-level "Refresh now" action deliberately does NOT write this field: a manual run is extra
    /// work on top of the schedule, not a replacement for it, so it must not delay the next
    /// scheduled poll.
    /// </summary>
    public DateTime? LastIngestionRunUtc { get; set; }

    public List<Session> Sessions { get; } = [];
    public List<UserTeam> UserTeams { get; } = [];

    public bool IsExamToolsConfigured =>
        !string.IsNullOrWhiteSpace(ExamToolsTeamCode)
        && !string.IsNullOrWhiteSpace(ExamToolsUsername)
        && !string.IsNullOrWhiteSpace(ExamToolsPassword);

    public bool IsZoomConfigured =>
        !string.IsNullOrWhiteSpace(ZoomAccountId)
        && !string.IsNullOrWhiteSpace(ZoomClientId)
        && !string.IsNullOrWhiteSpace(ZoomClientSecret);

    /// <summary>Just the per-Team half of Discord readiness (has a Guild been picked) — combine with IDiscordEventClient.IsConfigured (the shared bot's own readiness) before actually attempting Discord for a session.</summary>
    public bool IsDiscordConfigured => DiscordGuildId is not null && DiscordGuildId != 0;

    /// <summary>Matches the pre-multi-team ISquareClient.IsConfigured check — AccessToken only. LocationId is validated separately, inside SquareClient, at the point it's actually needed.</summary>
    public bool IsSquareConfigured => !string.IsNullOrWhiteSpace(SquareAccessToken);

    /// <summary>Whether this team's Square webhook can be signature-verified at all — checked by the webhook route before even attempting verification, distinct from IsSquareConfigured (a team could have AccessToken set for creating payment links but not yet have registered/copied over its webhook subscription details, or vice versa).</summary>
    public bool IsSquareWebhookConfigured =>
        !string.IsNullOrWhiteSpace(SquareWebhookSignatureKey)
        && !string.IsNullOrWhiteSpace(SquareWebhookNotificationUrl);

    /// <summary>Requires Host and Username, not just Host — same reasoning the pre-multi-team IEmailSender.IsConfigured had: a real default host could otherwise read as "configured" before credentials exist.</summary>
    public bool IsEmailConfigured =>
        !string.IsNullOrWhiteSpace(SmtpHost)
        && !string.IsNullOrWhiteSpace(SmtpUsername);
}
