using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// One-time (but safe to re-run — see below) sweep that upgrades every Team's plaintext credential
/// columns (ExamToolsPassword/ZoomClientSecret/SquareAccessToken/SquareWebhookSignatureKey/
/// SmtpPassword) to encrypted-at-rest, added 2026-07-30 alongside EncryptedStringConverter. Invoked
/// via the Worker's `--migrate-team-secrets` CLI flag (see Program.cs) — deliberately a
/// human-triggered one-off, not something that runs automatically on every startup, since it
/// touches every real team's live external-service credentials.
///
/// How it works: EncryptedStringConverter's read path always normalizes a column's in-memory value
/// to the true plaintext, whether the stored value was already encrypted or is still legacy
/// plaintext (it falls back to passing the raw value through on Unprotect failure rather than
/// throwing). So a completely normal `dbContext.Teams.ToListAsync()` already gives every Team's
/// true plaintext credential values, migrated or not — there is no need to read "around" the
/// converter via raw SQL. All this service does is force EF to re-save each populated credential
/// property (via EF.Property/IsModified, since re-setting a property to its own already-equal value
/// wouldn't otherwise register as a change), which runs it through the converter's write path
/// (always-encrypt) regardless of whether it needed upgrading.
///
/// Idempotent and safe to re-run any number of times (a legitimate ongoing use, not just for this
/// one initial rollout — see the "recovery" scenarios documented in docs/credential-encryption.md:
/// restoring an old pre-migration backup, an interrupted first run, or a credential set via a raw
/// DB edit bypassing the app entirely). Running it against already-encrypted data is a no-op in
/// effect (read normalizes to the same plaintext, write re-encrypts it to a new ciphertext blob —
/// never double-encrypted, since the read path always decrypts back to one true plaintext first).
/// </summary>
public class TeamSecretsMigrationService(AppDbContext dbContext, ILogger<TeamSecretsMigrationService> logger)
{
    private static readonly string[] CredentialPropertyNames =
    [
        nameof(Entities.Team.ExamToolsPassword),
        nameof(Entities.Team.ZoomClientSecret),
        nameof(Entities.Team.SquareAccessToken),
        nameof(Entities.Team.SquareWebhookSignatureKey),
        nameof(Entities.Team.SmtpPassword)
    ];

    /// <summary>Returns the number of teams that had at least one credential re-saved.</summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        var teams = await dbContext.Teams.ToListAsync(cancellationToken);
        var teamsTouched = 0;

        foreach (var team in teams)
        {
            var entry = dbContext.Entry(team);
            var touchedThisTeam = false;

            foreach (var propertyName in CredentialPropertyNames)
            {
                var property = entry.Property(propertyName);
                if (property.CurrentValue is not null)
                {
                    // Forces a write even though the CLR value looks unchanged to EF's change
                    // tracker — the point is to run it back through the converter's encrypt path.
                    property.IsModified = true;
                    touchedThisTeam = true;
                }
            }

            if (touchedThisTeam)
            {
                teamsTouched++;
            }
        }

        if (teamsTouched > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Team secrets migration: re-saved credential column(s) for {TeamsTouched} of {TotalTeams} team(s) — every populated credential is rewritten each run regardless of whether it needed it, so this number doesn't distinguish already-encrypted teams from newly-migrated ones",
            teamsTouched, teams.Count);
        return teamsTouched;
    }
}
