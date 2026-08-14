using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Authorization;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// Where an emailed reset link lands. The token arrives in the query string and is round-tripped
/// through a hidden field so the POST can validate it — Identity validates signature, purpose,
/// expiry and the user's current security stamp, so a used token dies with the stamp rotation that
/// a successful reset causes.
///
/// [AllowAnonymous] is load-bearing: the whole point is that the user cannot sign in.
/// </summary>
[AllowAnonymous]
public class ResetPasswordModel(PasswordResetService passwordResetService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public class InputModel
    {
        // [Range], not [Required] (L-10). A non-nullable int always satisfies Required — the same
        // trap CLAUDE.md documents for bool — so it read as server-side enforcement and was purely
        // decorative. Not exploitable either way, since 0 is never a real key and the token check
        // fails regardless; the problem is a guard that looks like one and is not.
        [Range(1, int.MaxValue)]
        public int UserId { get; set; }

        [Required]
        public string Token { get; set; } = "";

        // Length mirrors Program.cs's Identity options (RequiredLength = 12) so the user is told
        // before the round-trip rather than after. Identity remains the actual enforcement.
        [Required]
        [StringLength(100, MinimumLength = 12, ErrorMessage = "Your new password must be at least 12 characters.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = "";

        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The two passwords don't match.")]
        public string ConfirmPassword { get; set; } = "";
    }

    public IActionResult OnGet(int? userId, string? token)
    {
        if (userId is null || string.IsNullOrWhiteSpace(token))
        {
            return RedirectToPage("/Account/Login");
        }

        Input.UserId = userId.Value;
        Input.Token = token;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await passwordResetService.ResetAsync(Input.UserId, Input.Token, Input.NewPassword, CancellationToken.None);
        if (!result.Succeeded)
        {
            Errors = result.Errors;
            return Page();
        }

        TempData["StatusMessage"] = "Your password has been reset. You can sign in with it now.";
        return RedirectToPage("/Account/Login");
    }
}
