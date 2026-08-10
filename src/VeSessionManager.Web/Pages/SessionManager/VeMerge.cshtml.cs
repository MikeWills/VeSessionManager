using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Reviewing and merging two VE records that are one person (issue #142).
///
/// <para>The pairing is offered, never assumed. Candidates are found by shared call sign or shared
/// FRN, and the two are very different kinds of evidence: an FRN match is <b>proof</b>, since FCC
/// issues one per person, while a call sign match is only a suggestion — call signs get reissued.
/// The page says which it is rather than presenting one confidence for both.</para>
///
/// <para>Nothing merges without an explicit confirmation carrying the real counts. The action is
/// effectively irreversible, and "are you sure?" without numbers is not consent.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class VeMergeModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    VolunteerExaminerMergeService mergeService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public VolunteerExaminer Person { get; private set; } = null!;
    public IReadOnlyList<MergeCandidate> Candidates { get; private set; } = [];

    /// <param name="IsProof">True for an FRN match. FCC issues one FRN per person, so this is conclusive; a shared call sign is not.</param>
    public record MergeCandidate(VolunteerExaminer Other, bool IsProof, string Evidence, VeMergePreview Preview);

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        return loaded ?? Page();
    }

    public async Task<IActionResult> OnPostMergeAsync(int duplicateId)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        // The posted id must be one this page actually offered — otherwise any two records reachable
        // by URL could be merged, and a merge is not something to leave to a guessed parameter.
        if (Candidates.All(c => c.Other.Id != duplicateId))
        {
            return Forbid();
        }

        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        var result = await mergeService.MergeAsync(Id, duplicateId, user.Id, HttpContext.RequestAborted);

        if (result == VeMergeResult.Success)
        {
            TempData["StatusMessage"] = "Records merged. The duplicate has been retired and its history now sits on this record.";
            return RedirectToPage("/SessionManager/VeDetail", new { id = Id });
        }

        TempData["ErrorMessage"] = result switch
        {
            VeMergeResult.DifferentFrns => "These records hold different FRNs, so FCC considers them different people. Not merged.",
            VeMergeResult.SessionHistoryWouldChange => "Refused: the merge would have changed the session history. Nothing was changed.",
            VeMergeResult.SameRecord => "That is the same record.",
            _ => "Could not merge those records."
        };
        return RedirectToPage(new { id = Id });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        var person = await dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships)
            .FirstOrDefaultAsync(v => v.Id == Id, HttpContext.RequestAborted);
        if (person is null)
        {
            return NotFound();
        }

        var viewableTeamIds = accessScope.ResolveViewableTeamIds(user, null);
        // null means every team for a SystemAdmin — see CLAUDE.md's note on this exact guard.
        if (viewableTeamIds is not null && !person.TeamMemberships.Any(m => viewableTeamIds.Contains(m.TeamId)))
        {
            return Forbid();
        }

        Person = person;

        var others = await dbContext.VolunteerExaminers
            .Where(v => v.Id != Id
                        && ((v.CallSign != null && v.CallSign == person.CallSign)
                            || (v.Frn != null && person.Frn != null && v.Frn == person.Frn)
                            || (v.Frn != null && person.ConflictingFrn != null && v.Frn == person.ConflictingFrn)
                            || (person.Frn != null && v.ConflictingFrn != null && person.Frn == v.ConflictingFrn)))
            .ToListAsync(HttpContext.RequestAborted);

        var candidates = new List<MergeCandidate>();
        foreach (var other in others)
        {
            // Three ways two records can share an FRN, and all of them are proof: both hold it (only
            // possible before the unique index existed), or one holds it and the other recorded the
            // collision the sweep found — which is the usual case, since the index is what stopped
            // the second one storing it.
            var sharesFrn =
                (other.Frn is not null && other.Frn == person.Frn)
                || (other.Frn is not null && other.Frn == person.ConflictingFrn)
                || (person.Frn is not null && person.Frn == other.ConflictingFrn);
            // A shared placeholder call sign is not a match at all — every VE ExamTools has no call
            // sign for carries the same "<UNKNOWN>", and offering to merge them would be offering to
            // fuse strangers.
            var sharesCallSign = CallSign.IsUsable(person.CallSign) && other.CallSign == person.CallSign;
            if (!sharesFrn && !sharesCallSign)
            {
                continue;
            }

            var (result, preview) = await mergeService.PreviewAsync(Id, other.Id, HttpContext.RequestAborted);
            if (result != VeMergeResult.Success || preview is null)
            {
                continue;
            }

            candidates.Add(new MergeCandidate(
                other,
                sharesFrn,
                sharesFrn
                    ? $"Both records resolve to FRN {other.Frn ?? person.Frn}. FCC issues one per person, so these are the same person."
                    : $"Both records use the call sign {other.CallSign}. Call signs are reissued, so check the names before merging.",
                preview));
        }

        Candidates = candidates;
        return null;
    }
}
