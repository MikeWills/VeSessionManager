using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>Static "check your email" page. Shown identically whether or not an account exists — see PasswordResetService.</summary>
[AllowAnonymous]
public class ForgotPasswordConfirmationModel : PageModel;
