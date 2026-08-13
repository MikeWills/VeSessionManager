using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Worker;

/// <summary>
/// The Worker's <c>--verify-keyring</c> switch: run <see cref="DataProtectionKeyRingGuard"/> and
/// report the verdict as an exit code, starting nothing else.
///
/// <para><b>Why the switch exists.</b> The guard already runs on every normal startup, so "is the
/// key ring right?" was only ever answerable by booting a host — which starts nine background jobs
/// against whatever credentials are in the database. On a *restored* database those credentials are
/// live, so the only way to prove a backup was to poll ExamTools, create Zoom/Discord events and
/// mail real candidates. See BackupScripts' <c>runbooks/restore.md</c>.</para>
///
/// <para><b>Why this is its own type rather than a branch in Program.cs.</b> Top-level statements
/// are unreachable from the test projects, and the interesting behaviour here is not the guard —
/// which has its own tests — but the three decisions layered on top of it: no migration, zero teams
/// is a failure, and an exit code instead of an exception.</para>
/// </summary>
internal static class KeyRingVerification
{
    /// <summary>
    /// Returns 0 when every stored credential is readable, 1 otherwise.
    /// </summary>
    /// <param name="error">
    /// Where a failure is written. Injected rather than reaching for <c>Console.Error</c> so the
    /// message itself is assertable — the message is most of this command's value, since the caller
    /// is a person mid-restore deciding what to do next.
    /// </param>
    internal static async Task<int> RunAsync(
        AppDbContext dbContext,
        ILogger logger,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        // Deliberately no Database.Migrate() anywhere in this path. A check meant to be safe to run
        // on any schedule must not write to the database it is checking, and a restored backup older
        // than this binary should be reported rather than silently upgraded by the act of verifying
        // it. It is also why a missing/old schema below is reported as "could not complete".
        int teamCount;
        try
        {
            teamCount = await dbContext.Teams.CountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(CouldNotComplete(ex));
            return 1;
        }

        // The guard passes when it finds nothing *unreadable*, so an empty database verifies without
        // checking anything. That is correct for a startup guard — a fresh deployment has no
        // credentials to be wrong about — and useless as proof that a restore worked, which is what
        // this command is for. DataProtectionKeyRingGuardTests.NoTeams_Passes documents the pairing.
        if (teamCount == 0)
        {
            await error.WriteLineAsync(
                "Nothing to verify: this database contains no teams, so no stored credential was checked. " +
                "If this is a restored backup, the restore did not bring back the data you expected.");
            return 1;
        }

        try
        {
            await DataProtectionKeyRingGuard.VerifyAsync(dbContext, logger, cancellationToken);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            // The guard's own verdict: it read the credentials and they are not decryptable by this
            // process. Its message already says what to do and what not to do, so it is passed
            // through whole rather than summarized.
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(CouldNotComplete(ex));
            return 1;
        }
    }

    /// <summary>
    /// A check that could not run is not a check that failed, and the two call for different next
    /// steps — one is "restore a different key ring", the other is "your invocation is wrong."
    /// </summary>
    private static string CouldNotComplete(Exception ex) =>
        $"Could not verify the key ring — the check did not complete: {ex.Message}";
}
