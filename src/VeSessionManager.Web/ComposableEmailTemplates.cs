using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Which templates a hand-composed <b>candidate</b> email can start from, and what they are called.
///
/// <para>One home because two screens ask the same question now: the compose screen itself, and the
/// session's "Email candidates" menu, which offers them as shortcuts straight into it. Two copies of
/// this list would drift the moment a template was added to one.</para>
/// </summary>
public static class ComposableEmailTemplates
{
    /// <summary>
    /// The shipped templates that make sense to send by hand, in the order they are offered.
    ///
    /// <para>Deliberately not every seeded key. The felony-disclosure and youth-program emails carry
    /// per-candidate applicability rules (#221, #274) and stay the single-candidate buttons they
    /// already are — bulk is the shape #221 moved away from — and the payment-expiration notice goes
    /// to the team's own admin address rather than to a candidate.</para>
    /// </summary>
    public static readonly string[] Keys =
        ["GettingStartedLocally", "RegistrationConfirmation", "DayBeforeReminder"];

    /// <param name="Key">Empty for the blank draft — the case no template anticipated.</param>
    public record Choice(string Key, string Label);

    /// <summary>
    /// What this team can start from: the shipped ones it has, in <see cref="Keys"/> order so the
    /// list opens with the one the screen was built for, then its own candidate-facing templates
    /// alphabetically, since nothing ranks those.
    /// </summary>
    public static async Task<IReadOnlyList<Choice>> LoadAsync(
        AppDbContext dbContext, int teamId, CancellationToken cancellationToken)
    {
        var templates = await dbContext.EmailTemplates
            .Where(t => t.TeamId == teamId
                && (Keys.Contains(t.Key)
                    || (t.IsUserDefined && t.Audience == EmailTemplateAudience.Candidates)))
            .Select(t => new { t.Key, t.IsUserDefined, t.DisplayName })
            .ToListAsync(cancellationToken);

        return
        [
            .. Keys
                .Where(key => templates.Any(t => t.Key == key))
                .Select(key => new Choice(key, EmailTemplateLabels.For(key))),
            .. templates
                .Where(t => t.IsUserDefined)
                .Select(t => new Choice(t.Key, t.DisplayName ?? EmailTemplateLabels.For(t.Key)))
                .OrderBy(c => c.Label)
        ];
    }
}
