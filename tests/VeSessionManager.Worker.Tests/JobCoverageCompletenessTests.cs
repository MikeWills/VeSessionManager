using Microsoft.Extensions.Hosting;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// Every background job must have its tick driven by at least one test (issue #325).
///
/// <para><b>Why this guard exists.</b> The Worker had no test project at all until this audit, and
/// the reason it stayed that way is not that anyone decided against it — each job is short, reads as
/// obviously correct, and adding one more beside its neighbours never feels like the moment to start
/// testing them. A per-job checklist in a document would go stale the first time someone added a job
/// in a hurry. This does not: a new job fails the build until its tick has been driven once.</para>
///
/// <para>It deliberately asks for very little — that a test file mentions the job type. It cannot
/// judge whether the test is any good, and does not pretend to. What it prevents is the specific
/// thing that actually happened here: a job existing for months with nothing ever having run it.</para>
/// </summary>
public class JobCoverageCompletenessTests
{
    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!;
    }

    /// <summary>Concrete <c>BackgroundService</c> subclasses in the Worker — abstract bases excluded.</summary>
    private static List<Type> JobClasses() =>
        [.. typeof(JobTick).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(BackgroundService).IsAssignableFrom(t))
            .OrderBy(t => t.Name)];

    private static string AllTestSource()
    {
        var testsRoot = Path.Combine(
            RepositoryRoot().FullName, "tests", "VeSessionManager.Worker.Tests");

        var files = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     // Excluding this file is the point: it names every job type in its own failure
                     // message, which would otherwise make it satisfy itself.
                     && Path.GetFileName(f) != "JobCoverageCompletenessTests.cs")
            .ToList();

        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(File.ReadAllText));
    }

    [Fact]
    public void EveryJobClassIsExercisedBySomeTest()
    {
        var source = AllTestSource();

        var untested = JobClasses()
            .Where(t => !source.Contains(t.Name, StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();

        Assert.True(untested.Count == 0,
            "These jobs run in production and nothing has ever driven a tick of them:\n  "
            + string.Join("\n  ", untested));
    }

    /// <summary>
    /// Non-vacuity, and the failure this test class is most likely to have: if the reflection sweep
    /// found nothing, or the source read came back empty, the check above passes by comparing two
    /// empty sets.
    /// </summary>
    [Fact]
    public void TheDiscoveryFindsTheJobsAndTheTestSource()
    {
        // Nine concrete jobs. PerTeamDailyJob is a tenth BackgroundService and is deliberately not
        // counted: it is the abstract base three of the nine derive from, and it never runs on its own.
        Assert.True(JobClasses().Count >= 9, $"the Worker has nine jobs; reflection found {JobClasses().Count}");
        Assert.Contains(nameof(UlsWatcherJob), JobClasses().Select(t => t.Name));
        Assert.True(AllTestSource().Length > 10_000, "the test-source sweep read almost nothing");
    }
}
