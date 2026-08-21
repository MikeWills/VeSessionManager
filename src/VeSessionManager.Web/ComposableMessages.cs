using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Which messages a hand-composed email can start from.
///
/// <para>One home because two screens ask the same question: the compose screen itself, and the
/// session's "Email candidates" menu, which offers them as shortcuts straight into it. Two copies of
/// this list would drift the moment a message was added to one.</para>
///
/// <para><b>Replaced <c>ComposableEmailTemplates</c> (2026-08-21).</b> It listed shipped template keys
/// plus a team's own candidate-audience templates; there are no templates now, so it lists the
/// messages on the matching manual trigger.</para>
///
/// <para>⚠️ <b>The automated messages are deliberately no longer offered as starting text.</b> The old
/// list included the registration confirmation and the day-before reminder, which read as a
/// convenience — but their bodies are written around tokens the manual path does not supply
/// (<c>{{ZoomJoinUrl}}</c>, <c>{{PaymentLinkUrl}}</c>). Starting from one produced a draft whose
/// tokens render blank and whose send <i>succeeds</i>, which is precisely the class of failure this
/// whole change removes. A manual message is written against the manual trigger, and the tags shown
/// while writing it are the ones that will resolve.</para>
/// </summary>
public static class ComposableMessages
{
    /// <param name="Id">The <see cref="MessageRule"/> to start from. Zero for the blank draft — the case no message anticipated.</param>
    public record Choice(int Id, string Label);

    /// <summary>
    /// What this team can start from, alphabetically — nothing ranks them, and the old list's ordering
    /// existed only to put the shipped ones first.
    /// </summary>
    /// <param name="trigger">
    /// <c>ManualToCandidate</c> or <c>ManualToVe</c>. A message on any other trigger is fired rather
    /// than offered, and its tokens would not resolve here.
    /// </param>
    public static async Task<IReadOnlyList<Choice>> LoadAsync(
        AppDbContext dbContext, int teamId, MessageTrigger trigger, CancellationToken cancellationToken)
    {
        // Switched-off ones are excluded: for a manual message "off" means "not offered", since there
        // is no scan to stop. That is the whole difference in what the flag means here.
        var messages = await dbContext.MessageRules
            .AsNoTracking()
            .Where(r => r.TeamId == teamId && r.Trigger == trigger && r.IsEnabled)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(cancellationToken);

        return [.. messages.Select(m => new Choice(m.Id, m.Name)).OrderBy(c => c.Label)];
    }
}
