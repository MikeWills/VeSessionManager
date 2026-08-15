namespace VeSessionManager.Core.Admin;

/// <summary>Why a delete was refused, or that it succeeded (#188).</summary>
public enum UserDeleteOutcome
{
    Deleted,
    NotFound,

    /// <summary>Deleting the account you are signed in as would end your own session mid-request.</summary>
    CannotDeleteSelf,

    /// <summary>
    /// The last account that can sign in. Mirrors the startup guard in Web's Program.cs exactly —
    /// "can anyone sign in" (<c>PasswordHash != null</c>), not "does a user exist", because the
    /// Worker's dev seeder creates a passwordless System user to own audit foreign keys. Deleting
    /// past this point locks the deployment out of itself, and recovery is a command-line
    /// <c>--create-admin</c> run on the box.
    /// </summary>
    LastSignInCapableAccount,

    /// <summary>The account has done things. <see cref="UserDeleteResult.Blockers"/> says what.</summary>
    HasHistory
}

/// <param name="Blockers">
/// Human-readable, and the reason this type exists rather than a bare enum: #188 asks that a refusal
/// <b>name</b> what is in the way ("has 3 sessions marked complete"), not merely decline. An admin
/// told only "cannot delete" has no next step; one told which records reference the account can
/// decide whether to reassign, or to deactivate instead.
/// </param>
public record UserDeleteResult(UserDeleteOutcome Outcome, IReadOnlyList<string> Blockers)
{
    public static UserDeleteResult Deleted() => new(UserDeleteOutcome.Deleted, []);
    public static UserDeleteResult Refused(UserDeleteOutcome outcome) => new(outcome, []);
}
