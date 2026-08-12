using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: create Teams and edit a Team's per-integration credentials (ExamTools/Zoom/Discord/
/// Square/SMTP) plus its EmailSettings row. Secret-field semantics on every UpdateX method: a null
/// argument means "leave unchanged," a non-null argument (including empty string) means "set to
/// this value" — pages never echo a stored secret back into an input, they show a masked
/// placeholder driven by Team.IsXConfigured, and only pass a value through when the admin actually
/// typed a new one. Audit Details never contain the secret value itself.
/// </summary>
public class TeamSettingsService(AppDbContext dbContext, TimeProvider timeProvider, ILogger<TeamSettingsService> logger)
{
    public async Task<(TeamActionResult Result, Team? Team)> CreateAsync(string name, int userId, CancellationToken cancellationToken)
    {
        // Guarded here, not on the page: Team.Name is a required column, so null gives an unhandled
        // 500 and "" succeeds and leaves a nameless team every screen renders as blank. A service
        // guard also covers every future caller, not just the one page that exists today (#275).
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            return (TeamActionResult.NameRequired, null);
        }

        if (await dbContext.Teams.AnyAsync(t => t.Name == name, cancellationToken))
        {
            return (TeamActionResult.DuplicateName, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Atomic where the provider allows it (issue #287). The two saves below cannot be collapsed
        // — the audit needs team.Id, which does not exist until the first one — and a failure
        // between them is not merely a lost audit row here: the seeding sits in the middle, so it
        // would leave a committed team with no EmailSettings and no templates. That is exactly the
        // silently-non-functional-for-email state the seeding was moved into this method to prevent,
        // and it is not self-healing from the Web process.
        return await AtomicWrite.RunAsync(dbContext, async () =>
        {
            var team = new Team { Name = name, CreatedUtc = now };
            dbContext.Teams.Add(team);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Seed the team's EmailSettings row and default templates immediately (2026-08-04). These
            // used to appear only when the Worker next started, so a team created here was silently
            // non-functional for email until someone restarted a different process — the Email Templates
            // page read "No templates seeded for this team yet", and CandidateNotificationService skipped
            // the team with a single log line rather than sending anything. Idempotent, so the Worker's
            // startup sweep remains a harmless backfill.
            await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, logger, team);

            AddAudit(userId, "TeamCreated", team.Id, $"Team '{name}' created.", now);
            await dbContext.SaveChangesAsync(cancellationToken);

            return (TeamActionResult.Success, (Team?)team);
        }, cancellationToken);
    }

    public async Task<TeamActionResult> UpdateExamToolsAsync(int teamId, string? teamCode, string? username, string? password, string? baseUrl, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        // Validated BEFORE anything is assigned (issue #258), so a rejected edit leaves the stored
        // settings exactly as they were. Half-applying a rejected change would break ingestion on a
        // failed attempt, which is its own denial of service.
        if (!string.IsNullOrWhiteSpace(baseUrl) && !IsAcceptableExamToolsBaseUrl(baseUrl))
        {
            return TeamActionResult.InvalidExamToolsBaseUrl;
        }

        team.ExamToolsTeamCode = teamCode;
        team.ExamToolsUsername = username;
        team.ExamToolsBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl;
        // Null means "leave the stored password alone" — the form posts blank for an unchanged
        // secret. Blank-but-not-null means "clear it", and clearing stores null rather than ""
        // (#279): "" round-trips through the encrypting converter as ciphertext, so a cleared
        // password read as a set one everywhere that asks `!= null` instead of IsNullOrWhiteSpace.
        if (password is not null)
        {
            team.ExamToolsPassword = string.IsNullOrWhiteSpace(password) ? null : password;
        }

        return await SaveTeamUpdateAsync(team, "TeamExamToolsCredentialsUpdated", userId, cancellationToken);
    }

    public async Task<TeamActionResult> UpdateZoomAsync(int teamId, string? accountId, string? clientId, string? clientSecret, string? zoomUserId, int breakoutRoomCount, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        team.ZoomAccountId = accountId;
        team.ZoomClientId = clientId;
        team.ZoomUserId = zoomUserId;
        team.ZoomBreakoutRoomCount = Math.Max(0, breakoutRoomCount);
        if (clientSecret is not null)
        {
            team.ZoomClientSecret = clientSecret;
        }

        return await SaveTeamUpdateAsync(team, "TeamZoomCredentialsUpdated", userId, cancellationToken);
    }

    public async Task<TeamActionResult> UpdateDiscordAsync(int teamId, ulong? guildId, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        team.DiscordGuildId = guildId;

        return await SaveTeamUpdateAsync(team, "TeamDiscordSettingsUpdated", userId, cancellationToken);
    }

    public async Task<TeamActionResult> UpdateSquareAsync(int teamId, string? accessToken, string? locationId, string? webhookSignatureKey, string? webhookNotificationUrl, SquareApiEnvironment environment, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        team.SquareLocationId = locationId;
        team.SquareEnvironment = environment;
        team.SquareWebhookNotificationUrl = webhookNotificationUrl;
        if (accessToken is not null)
        {
            team.SquareAccessToken = accessToken;
        }
        if (webhookSignatureKey is not null)
        {
            team.SquareWebhookSignatureKey = webhookSignatureKey;
        }

        return await SaveTeamUpdateAsync(team, "TeamSquareCredentialsUpdated", userId, cancellationToken);
    }

    public async Task<TeamActionResult> UpdateSmtpAsync(int teamId, string? host, int? port, string? username, string? password, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        // Validated before assignment, same reasoning as UpdateExamToolsAsync (issue #259).
        if (!string.IsNullOrWhiteSpace(host) && !IsAcceptableSmtpHost(host))
        {
            return TeamActionResult.InvalidSmtpHost;
        }

        // Blank normalizes to null, matching ExamToolsBaseUrl above. Storing "" instead of null is
        // the inconsistency #279 is about: every consumer here happens to use IsNullOrWhiteSpace, so
        // it works — but a single `!= ""` comparison anywhere would then behave differently
        // depending on which field it was written through.
        team.SmtpHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        team.SmtpPort = port;
        team.SmtpUsername = username;
        if (password is not null)
        {
            team.SmtpPassword = password;
        }

        return await SaveTeamUpdateAsync(team, "TeamSmtpCredentialsUpdated", userId, cancellationToken);
    }

    /// <summary>Largest logo accepted. A logo is a small branding image; anything approaching this is a photo pasted in by mistake, and every byte here is added to every email the team ever sends.</summary>
    public const int MaxLogoBytes = 200 * 1024;

    /// <summary>
    /// Stores (or, with null <paramref name="content"/>, clears) the team's email logo.
    ///
    /// <para><b>The content type is derived from the bytes, never from the upload's declared
    /// type.</b> A browser-supplied Content-Type is attacker-controlled and trivially spoofed, so
    /// trusting it would let anything at all be stored and then served to mail clients under an
    /// image label. The two magic-number checks below are the whole allowlist: PNG and JPEG. SVG is
    /// deliberately excluded — mail clients broadly do not render it, and an SVG is an executable
    /// document that would be a stored-XSS vector anywhere it were ever served back.</para>
    /// </summary>
    public async Task<TeamActionResult> UpdateLogoAsync(int teamId, byte[]? content, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        if (content is null || content.Length == 0)
        {
            team.LogoBytes = null;
            team.LogoContentType = null;
            team.LogoUpdatedUtc = null;
            return await SaveTeamUpdateAsync(team, "TeamLogoCleared", userId, cancellationToken);
        }

        if (content.Length > MaxLogoBytes)
        {
            return TeamActionResult.LogoTooLarge;
        }

        var contentType = SniffImageContentType(content);
        if (contentType is null)
        {
            return TeamActionResult.LogoUnsupportedFormat;
        }

        team.LogoBytes = content;
        team.LogoContentType = contentType;
        team.LogoUpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
        return await SaveTeamUpdateAsync(team, "TeamLogoUpdated", userId, cancellationToken);
    }

    /// <summary>Returns the MIME type implied by the file's own leading bytes, or null when it is neither PNG nor JPEG.</summary>
    private static string? SniffImageContentType(byte[] content)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (content.Length >= 8 &&
            content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 &&
            content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A)
        {
            return "image/png";
        }

        // JPEG: FF D8 FF — every JPEG variant (JFIF, Exif) shares this SOI + marker prefix.
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return null;
    }

    public async Task<TeamActionResult> UpdatePurgeSettingsAsync(int teamId, int purgeUnpaidLinkDays, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        team.PurgeUnpaidLinkDays = purgeUnpaidLinkDays;

        return await SaveTeamUpdateAsync(team, "TeamPurgeSettingsUpdated", userId, cancellationToken);
    }

    /// <summary>Upserts the Team's EmailSettings row (one per team, unique index on TeamId) — creates it if somehow missing (should already exist via EmailDefaultsSeeder), otherwise updates in place.</summary>
    public async Task<TeamActionResult> UpdateEmailSettingsAsync(int teamId, string fromAddress, string? fromDisplayName, string replyToAddress, string privacyPolicyUrl, string adminNotificationEmail, string? bccAddress, int userId, CancellationToken cancellationToken)
    {
        var teamExists = await dbContext.Teams.AnyAsync(t => t.Id == teamId, cancellationToken);
        if (!teamExists)
        {
            return TeamActionResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == teamId, cancellationToken);
        if (emailSettings is null)
        {
            emailSettings = new EmailSettings
            {
                TeamId = teamId,
                FromAddress = fromAddress,
                ReplyToAddress = replyToAddress,
                PrivacyPolicyUrl = privacyPolicyUrl,
                AdminNotificationEmail = adminNotificationEmail
            };
            dbContext.EmailSettings.Add(emailSettings);
        }

        emailSettings.FromAddress = fromAddress;
        emailSettings.FromDisplayName = fromDisplayName;
        emailSettings.ReplyToAddress = replyToAddress;
        emailSettings.PrivacyPolicyUrl = privacyPolicyUrl;
        emailSettings.AdminNotificationEmail = adminNotificationEmail;
        // Blank stores null rather than "", so "is a BCC configured?" is one check everywhere.
        emailSettings.BccAddress = string.IsNullOrWhiteSpace(bccAddress) ? null : bccAddress.Trim();
        emailSettings.UpdatedByUserId = userId;
        emailSettings.UpdatedUtc = now;

        // Names whether the BCC is on, not the address itself: turning candidate-mail monitoring on
        // or off is the part worth being able to reconstruct later.
        AddAudit(userId, "TeamEmailSettingsUpdated", teamId,
            $"Team {teamId} email settings updated. Candidate BCC {(emailSettings.BccAddress is null ? "off" : "on")}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return TeamActionResult.Success;
    }

    /// <summary>
    /// Hosts a team may point ExamTools at. Registrable domains, matched exactly or as a subdomain —
    /// so <c>alpha.exam.tools</c> passes and <c>exam.tools.attacker.example</c> does not, which a
    /// naive <c>EndsWith</c> would wave through.
    /// </summary>
    public static readonly string[] AllowedExamToolsDomains = ["exam.tools", "examtools.dev"];

    /// <summary>
    /// Whether a team may store this as its ExamTools base URL (issue #258).
    ///
    /// <para><b>An allowlist, not merely a scheme check.</b> Secrets in this app are write-only —
    /// the settings page shows a masked placeholder and never echoes the stored value — so an admin
    /// who does not know the ExamTools password can still cause it to be sent somewhere, by changing
    /// only the host and leaving the password field blank (which means "keep existing"). Rejecting
    /// private addresses would stop the SSRF half and leave the credential-exfiltration half wide
    /// open; only an allowlist closes it.</para>
    ///
    /// <para>The list is the deployment's real ExamTools hosts, which is a closed set: this is an
    /// override for pointing a dev team at a different ExamTools instance, not a general URL field.
    /// Adding a host is a deliberate code change, which is the right weight for it.</para>
    /// </summary>
    public static bool IsAcceptableExamToolsBaseUrl(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            // Also covers the malformed case that used to surface as an unhandled
            // UriFormatException from `new Uri(...)` inside a background job, far from the page
            // that accepted the value.
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        return AllowedExamToolsDomains.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a team may store this as its SMTP host (issue #259).
    ///
    /// <para>No allowlist here, deliberately: teams legitimately use their own mail providers, so
    /// naming them in code would be wrong. What is excluded is everything that cannot be a real
    /// external mail server — anything resolving to loopback, link-local (including cloud metadata at
    /// <c>169.254.169.254</c>), or RFC1918 space — plus values that are not a bare host at all.</para>
    ///
    /// <para><b>This is a smaller guarantee than #258's, and knowingly so.</b> It stops the SSRF and
    /// the obvious internal-network probe; it does not stop an admin naming a mail server they
    /// control, which no host check can. The residual risk is bounded by SMTP credentials being
    /// per-team and by the audit entry the change writes.</para>
    /// </summary>
    public static bool IsAcceptableSmtpHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();

        // A bare host, not a URL and not something with spaces or a path. Uri.CheckHostName returns
        // Unknown for both, which is exactly the distinction wanted here.
        var hostType = Uri.CheckHostName(trimmed);
        if (hostType == UriHostNameType.Unknown)
        {
            return false;
        }

        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A literal address is checked directly. A DNS name is not resolved: resolution here would be
        // a network call on a form POST, would give a different answer at send time anyway, and would
        // make the validation itself an SSRF primitive.
        if (System.Net.IPAddress.TryParse(trimmed, out var address))
        {
            return !IsInternal(address);
        }

        return hostType == UriHostNameType.Dns && trimmed.Contains('.');
    }

    private static bool IsInternal(System.Net.IPAddress address)
    {
        if (System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,                                        // 10.0.0.0/8
            127 => true,                                       // 127.0.0.0/8
            169 when octets[1] == 254 => true,                 // 169.254.0.0/16, incl. cloud metadata
            172 when octets[1] >= 16 && octets[1] <= 31 => true,// 172.16.0.0/12
            192 when octets[1] == 168 => true,                 // 192.168.0.0/16
            0 => true,                                         // 0.0.0.0/8
            _ => false
        };
    }

    private async Task<TeamActionResult> SaveTeamUpdateAsync(Team team, string action, int userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(userId, action, team.Id, $"Team {team.Id} credentials updated ({action}).", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return TeamActionResult.Success;
    }

    private void AddAudit(int userId, string action, int entityId, string details, DateTime now) =>
        dbContext.AddAuditLog(userId, action, nameof(Team), entityId, details, now);
}

public enum TeamActionResult
{
    Success,
    NotFound,
    DuplicateName,

    /// <summary>A required value arrived blank — see RequiredInputGuardTests for why this is checked here rather than on the page (issue #275).</summary>
    NameRequired,

    /// <summary>Uploaded logo exceeded <see cref="TeamSettingsService.MaxLogoBytes"/>.</summary>
    LogoTooLarge,

    /// <summary>
    /// The ExamTools base URL was not an absolute HTTPS URL on a known ExamTools host (issue #258).
    /// </summary>
    InvalidExamToolsBaseUrl,

    /// <summary>
    /// The SMTP host was not a plain, externally-routable hostname (issue #259).
    /// </summary>
    InvalidSmtpHost,

    /// <summary>Uploaded logo's own bytes were neither PNG nor JPEG, whatever the browser declared.</summary>
    LogoUnsupportedFormat
}
