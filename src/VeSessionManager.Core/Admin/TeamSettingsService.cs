using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
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
public class TeamSettingsService(AppDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<(TeamActionResult Result, Team? Team)> CreateAsync(string name, int userId, CancellationToken cancellationToken)
    {
        if (await dbContext.Teams.AnyAsync(t => t.Name == name, cancellationToken))
        {
            return (TeamActionResult.DuplicateName, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var team = new Team { Name = name, CreatedUtc = now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync(cancellationToken);

        AddAudit(userId, "TeamCreated", team.Id, $"Team '{name}' created.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (TeamActionResult.Success, team);
    }

    public async Task<TeamActionResult> UpdateExamToolsAsync(int teamId, string? teamCode, string? username, string? password, string? baseUrl, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        team.ExamToolsTeamCode = teamCode;
        team.ExamToolsUsername = username;
        team.ExamToolsBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl;
        if (password is not null)
        {
            team.ExamToolsPassword = password;
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

    public async Task<TeamActionResult> UpdateSquareAsync(int teamId, string? accessToken, string? locationId, string? webhookSignatureKey, string? webhookNotificationUrl, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        team.SquareLocationId = locationId;
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

    public async Task<TeamActionResult> UpdateSmtpAsync(int teamId, string? host, int? port, string? username, string? password, bool? useStartTls, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return TeamActionResult.NotFound;
        }

        team.SmtpHost = host;
        team.SmtpPort = port;
        team.SmtpUsername = username;
        team.SmtpUseStartTls = useStartTls;
        if (password is not null)
        {
            team.SmtpPassword = password;
        }

        return await SaveTeamUpdateAsync(team, "TeamSmtpCredentialsUpdated", userId, cancellationToken);
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
    public async Task<TeamActionResult> UpdateEmailSettingsAsync(int teamId, string fromAddress, string? fromDisplayName, string replyToAddress, string privacyPolicyUrl, string adminNotificationEmail, int userId, CancellationToken cancellationToken)
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
        emailSettings.UpdatedByUserId = userId;
        emailSettings.UpdatedUtc = now;

        AddAudit(userId, "TeamEmailSettingsUpdated", teamId, $"Team {teamId} email settings updated.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return TeamActionResult.Success;
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
    DuplicateName
}
