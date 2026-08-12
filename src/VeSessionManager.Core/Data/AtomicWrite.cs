using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace VeSessionManager.Core.Data;

/// <summary>
/// Makes a two-save unit of work atomic where the provider can, and a no-op where it cannot
/// (issue #287).
///
/// <para><b>Why two saves exist at all.</b> Several create paths must save the new row before they
/// can audit it, because <c>AuditLog.EntityId</c> is an <c>int</c> and the row has no id until then.
/// That leaves a window: if the audit save fails, the entity is committed with nothing in the trail
/// saying who created it — and for <c>TeamSettingsService.CreateAsync</c> it is worse than a missing
/// audit row, because the seeding of the team's <c>EmailSettings</c> and default templates happens
/// between the two, so a failure leaves a team that is silently non-functional for email. That is
/// precisely the state the seeding was moved into this method to prevent.</para>
///
/// <para><b>Why the provider check rather than an unconditional transaction.</b> EF's in-memory
/// provider does not support transactions — <c>BeginTransactionAsync</c> throws — and it is what most
/// of the service tests use. An unconditional transaction would mean either breaking those tests or
/// converting them all to SQLite, and neither is a good trade for a guard that only has teeth against
/// a real database anyway: with no transactional store, there is nothing to roll back. So relational
/// providers get the guarantee, and the in-memory one runs exactly as it did before.</para>
///
/// <para>The <c>VolunteerExaminerMergeService</c> is deliberately <b>not</b> a user of this: its
/// transaction is unconditional and its tests use real SQLite, because a half-finished merge is
/// unrecoverable and the guarantee has to be real there rather than best-effort.</para>
/// </summary>
public static class AtomicWrite
{
    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction when the provider supports one, committing
    /// on success and rolling back on any exception. Returns whatever the work returns.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        AppDbContext dbContext, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await work();
        }

        IDbContextTransaction? transaction = null;
        try
        {
            // An ambient transaction means a caller is already composing a larger unit of work; a
            // nested one would either throw or silently narrow their guarantee to ours.
            if (dbContext.Database.CurrentTransaction is null)
            {
                transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            }

            var result = await work();

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);

                // The database is back where it started; the change tracker is not. Without this the
                // context would report the failed write as applied for the rest of the request —
                // the same trap #234 recorded in the merge service.
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
