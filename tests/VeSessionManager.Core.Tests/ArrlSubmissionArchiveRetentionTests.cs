using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Retention;
using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Ageing out what was filed with ARRL (issue #197).
///
/// <para><b>Null means keep forever, and that is the shipped default</b> — the same opt-in rule as
/// every other retention setting here. Nobody's evidence starts disappearing because a job shipped,
/// and given Mike has had to go back to one of these after the fact, "keep forever" may well be the
/// right permanent answer rather than a placeholder.</para>
///
/// <para><b>The files go; the row stays.</b> The row is the record that a filing happened, which is
/// exactly what someone needs years later — including for a submission whose outcome could never be
/// confirmed.</para>
/// </summary>
public class ArrlSubmissionArchiveRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "vesm-arrlret-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private RecordRetentionService CreateService(AppDbContext db)
    {
        var options = Options.Create(new ArrlSubmissionOptions { ArchiveRootPath = root });
        return new RecordRetentionService(
            db,
            new SystemSettingsService(db, new FixedTimeProvider(Now)),
            new ArrlSubmissionArchiveStore(options, NullLogger<ArrlSubmissionArchiveStore>.Instance),
            new FixedTimeProvider(Now),
            NullLogger<RecordRetentionService>.Instance);
    }

    /// <summary>Writes a submission with real files on disk, aged by <paramref name="daysOld"/>.</summary>
    private async Task<ArrlVecSubmission> SeedAsync(AppDbContext db, int daysOld, int? retentionDays, bool withAttachment = false)
    {
        db.SystemSettings.Add(new SystemSettings { Id = 1, VecSubmissionArchiveRetentionDays = retentionDays });

        var archivePath = Path.Combine("MARC", "arrl", "2026", "04", "archive.zip");
        Directory.CreateDirectory(Path.Combine(root, Path.GetDirectoryName(archivePath)!));
        await File.WriteAllBytesAsync(Path.Combine(root, archivePath), [1, 2, 3]);

        string? attachmentPath = null;
        if (withAttachment)
        {
            attachmentPath = Path.Combine("MARC", "arrl", "2026", "04", "youth.pdf");
            await File.WriteAllBytesAsync(Path.Combine(root, attachmentPath), [4, 5]);
        }

        var submission = new ArrlVecSubmission
        {
            SubmittedUtc = Now.AddDays(-daysOld),
            FullName = "Mike Wills", CallSign = "WX0MIK", Email = "a@b.c", Phone = "1",
            SessionDate = "2026-04-21", Location = "Remote Online",
            PaymentMethod = ArrlPaymentMethod.CreditCardOnFile, AmountCharged = "8.00",
            ArchiveFileName = "archive.zip", ArchiveStoredPath = archivePath, ArchiveByteCount = 3,
            AttachmentFileName = withAttachment ? "youth.pdf" : null,
            AttachmentStoredPath = attachmentPath,
            ResponseBody = "<p>archive.zip has been uploaded successfully.</p>",
            Outcome = ArrlReceiptOutcome.Succeeded
        };
        db.ArrlVecSubmissions.Add(submission);
        await db.SaveChangesAsync();
        return submission;
    }

    [Fact]
    public async Task PastTheWindow_TheFilesAreDeletedAndTheRowIsMarked()
    {
        using var db = CreateContext();
        var submission = await SeedAsync(db, daysOld: 400, retentionDays: 365);

        var result = await CreateService(db).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.ArrlSubmissionArchivesPurged);
        Assert.False(File.Exists(Path.Combine(root, submission.ArchiveStoredPath!)));
        Assert.Equal(Now, (await db.ArrlVecSubmissions.SingleAsync()).FilesPurgedUtc);
    }

    /// <summary>
    /// The row survives its files. It is the record that a filing happened — and for an unconfirmed
    /// submission it is the only account of what went.
    /// </summary>
    [Fact]
    public async Task TheSubmissionRowItselfIsNeverDeleted()
    {
        using var db = CreateContext();
        await SeedAsync(db, daysOld: 400, retentionDays: 365);

        await CreateService(db).RunAsync(CancellationToken.None);

        var submission = await db.ArrlVecSubmissions.SingleAsync();
        Assert.Equal("archive.zip", submission.ArchiveFileName);
        Assert.Equal(ArrlReceiptOutcome.Succeeded, submission.Outcome);
    }

    [Fact]
    public async Task BothFilesGo()
    {
        using var db = CreateContext();
        var submission = await SeedAsync(db, daysOld: 400, retentionDays: 365, withAttachment: true);

        await CreateService(db).RunAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(root, submission.ArchiveStoredPath!)));
        Assert.False(File.Exists(Path.Combine(root, submission.AttachmentStoredPath!)));
    }

    [Fact]
    public async Task InsideTheWindow_NothingIsTouched()
    {
        using var db = CreateContext();
        var submission = await SeedAsync(db, daysOld: 10, retentionDays: 365);

        var result = await CreateService(db).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.ArrlSubmissionArchivesPurged);
        Assert.True(File.Exists(Path.Combine(root, submission.ArchiveStoredPath!)));
        Assert.Null((await db.ArrlVecSubmissions.SingleAsync()).FilesPurgedUtc);
    }

    /// <summary>
    /// Unset means keep forever, and it is the shipped default. Nobody's evidence starts disappearing
    /// because a job shipped — the same opt-in rule as the other retention settings.
    /// </summary>
    [Fact]
    public async Task WithNoRetentionConfigured_NothingIsEverPurged()
    {
        using var db = CreateContext();
        var submission = await SeedAsync(db, daysOld: 5000, retentionDays: null);

        var result = await CreateService(db).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.ArrlSubmissionArchivesPurged);
        Assert.True(File.Exists(Path.Combine(root, submission.ArchiveStoredPath!)));
    }

    /// <summary>
    /// <c>FilesPurgedUtc</c> is both the query filter and the idempotency guard, the same idiom as
    /// every other scan-based job here — a second run must not re-count what it already cleared.
    /// </summary>
    [Fact]
    public async Task ASecondRunDoesNothing()
    {
        using var db = CreateContext();
        await SeedAsync(db, daysOld: 400, retentionDays: 365);
        var service = CreateService(db);

        await service.RunAsync(CancellationToken.None);
        var second = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, second.ArrlSubmissionArchivesPurged);
    }

    /// <summary>
    /// A file already gone — deleted by hand, or by a run interrupted between the delete and the save
    /// — must still settle the row rather than retrying forever.
    /// </summary>
    [Fact]
    public async Task AFileThatIsAlreadyGone_StillSettlesTheRow()
    {
        using var db = CreateContext();
        var submission = await SeedAsync(db, daysOld: 400, retentionDays: 365);
        File.Delete(Path.Combine(root, submission.ArchiveStoredPath!));

        var result = await CreateService(db).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.ArrlSubmissionArchivesPurged);
        Assert.NotNull((await db.ArrlVecSubmissions.SingleAsync()).FilesPurgedUtc);
    }

    /// <summary>
    /// The stored path is kept after purging, deliberately: "there was an archive here and it aged
    /// out" is a different answer from "there never was one", and only the first is true.
    /// </summary>
    [Fact]
    public async Task ThePathIsKeptSoThePageCanSayItWasPurgedRatherThanMissing()
    {
        using var db = CreateContext();
        await SeedAsync(db, daysOld: 400, retentionDays: 365);

        await CreateService(db).RunAsync(CancellationToken.None);

        Assert.NotNull((await db.ArrlVecSubmissions.SingleAsync()).ArchiveStoredPath);
    }
}
