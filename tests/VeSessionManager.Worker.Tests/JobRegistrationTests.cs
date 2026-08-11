using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// Every background job must be three things at once: a class, a registration, and a
/// <see cref="JobSchedules"/> descriptor. Miss any one and the failure is silent (issue #325).
///
/// <list type="bullet">
///   <item><b>Class without registration</b> — the job simply never runs. Nothing logs, nothing
///   fails; the work quietly stops happening.</item>
///   <item><b>Registration without a descriptor</b> — the job runs but is invisible on the admin Job
///   Schedule page, which claims to be complete. <c>JobSchedules</c>' own doc says as much:
///   "Adding a job means adding it here too."</item>
///   <item><b>Descriptor without a registration</b> — the page advertises a schedule nobody obeys,
///   which is worse than an absent row: a confidently wrong screen.</item>
/// </list>
///
/// <para>Checked three ways because no single source knows all of it: reflection finds the classes,
/// a source scan of <c>Program.cs</c> finds the registrations (top-level statements cannot be
/// invoked from a test without starting a real host), and <c>JobSchedules.All</c> is the registry.
/// The source scan is the same shape as Core's <c>InlineEventHandlerTests</c>, for the same reason —
/// the mistake is someone not writing a line, which only the source can reveal.</para>
/// </summary>
public class JobRegistrationTests
{
    /// <summary>
    /// Descriptors that deliberately have no hosted service of their own because another job runs
    /// them inside its own tick. Each needs a reason, and the reason needs to survive review.
    /// </summary>
    private static readonly Dictionary<string, string> RunsInsideAnotherJob = new()
    {
        [JobSchedules.VeLicenseWatch] =
            "Runs inside LicenseWatchJob's tick, on the same anchored slot — same FCC data through " +
            "the same mirror. Listed separately because it writes its own JobRunHistory row, so the " +
            "Job Schedule page would otherwise report a job that visibly runs and has no schedule."
    };

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

    private static List<string> RegisteredJobNames()
    {
        var programPath = Path.Combine(RepositoryRoot().FullName, "src", "VeSessionManager.Worker", "Program.cs");
        Assert.True(File.Exists(programPath), $"Expected the Worker's Program.cs at {programPath}");

        return [.. Regex.Matches(File.ReadAllText(programPath), @"AddHostedService<(\w+)>\s*\(")
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n)];
    }

    /// <summary>
    /// The naming convention every job follows: <c>SessionIngestionJob</c> ⇄ <c>"SessionIngestion"</c>.
    /// Asserted rather than assumed by <see cref="EveryJobClassHasAMatchingScheduleDescriptor"/>, so
    /// a job that breaks it fails loudly here instead of silently skipping the checks below.
    /// </summary>
    private static string JobNameFor(Type jobClass) =>
        jobClass.Name.EndsWith("Job", StringComparison.Ordinal)
            ? jobClass.Name[..^"Job".Length]
            : jobClass.Name;

    [Fact]
    public void EveryJobClassIsRegisteredAsAHostedService()
    {
        var classes = JobClasses().Select(t => t.Name).ToList();
        var registered = RegisteredJobNames();

        var unregistered = classes.Except(registered).ToList();
        Assert.True(unregistered.Count == 0,
            "These BackgroundService classes exist but are never registered, so they simply never run — " +
            "nothing logs and nothing fails:\n  " + string.Join("\n  ", unregistered));

        // The other direction: a registration naming a class that no longer exists would not compile,
        // so this can only catch a stale *name* in a comment-like position. Cheap to assert anyway.
        var phantom = registered.Except(classes).ToList();
        Assert.True(phantom.Count == 0,
            "Registered but not a concrete BackgroundService in this assembly:\n  " + string.Join("\n  ", phantom));
    }

    [Fact]
    public void EveryJobClassHasAMatchingScheduleDescriptor()
    {
        var descriptorNames = JobSchedules.All.Select(d => d.JobName).ToHashSet();

        var missing = JobClasses()
            .Select(t => new { Class = t.Name, JobName = JobNameFor(t) })
            .Where(x => !descriptorNames.Contains(x.JobName))
            .ToList();

        Assert.True(missing.Count == 0,
            "These jobs run but have no JobSchedules descriptor, so the admin Job Schedule page — which " +
            "claims to list every job — cannot show them. Either add a descriptor or, if the class does " +
            "not follow the <Name>Job convention, this test needs to learn about it:\n  " +
            string.Join("\n  ", missing.Select(x => $"{x.Class} -> expected descriptor \"{x.JobName}\"")));
    }

    [Fact]
    public void EveryScheduleDescriptorIsBackedByAJobThatActuallyRuns()
    {
        var jobNames = JobClasses().Select(JobNameFor).ToHashSet();

        var unbacked = JobSchedules.All
            .Select(d => d.JobName)
            .Where(name => !jobNames.Contains(name) && !RunsInsideAnotherJob.ContainsKey(name))
            .ToList();

        Assert.True(unbacked.Count == 0,
            "The Job Schedule page would advertise these schedules with nothing obeying them — a " +
            "confidently wrong screen, which is worse than an absent row. Add the job, remove the " +
            "descriptor, or record it in RunsInsideAnotherJob with a reason:\n  " +
            string.Join("\n  ", unbacked));
    }

    /// <summary>
    /// Guards the exception list itself. If <c>VeLicenseWatch</c> ever gains its own hosted service,
    /// the entry above becomes a lie that would silently suppress the check for it.
    /// </summary>
    [Fact]
    public void TheRunsInsideAnotherJobExceptions_AreStillExceptions()
    {
        var jobNames = JobClasses().Select(JobNameFor).ToHashSet();
        var descriptorNames = JobSchedules.All.Select(d => d.JobName).ToHashSet();

        foreach (var (name, _) in RunsInsideAnotherJob)
        {
            Assert.True(descriptorNames.Contains(name),
                $"\"{name}\" is listed as running inside another job, but has no JobSchedules descriptor at all.");
            Assert.False(jobNames.Contains(name),
                $"\"{name}\" now has its own hosted service, so it is no longer an exception — remove it " +
                "from RunsInsideAnotherJob or the real check stays suppressed for it.");
        }
    }

    /// <summary>
    /// Non-vacuity. Every assertion above is "this set difference is empty", which passes trivially
    /// if the sets are empty — a broken discovery helper would look like a clean bill of health.
    /// </summary>
    [Fact]
    public void TheDiscoveryHelpers_ActuallyFindSomething()
    {
        Assert.NotEmpty(JobClasses());
        Assert.NotEmpty(RegisteredJobNames());
        Assert.NotEmpty(JobSchedules.All);

        // PerTeamDailyJob is abstract and must never be counted as a job in its own right.
        Assert.DoesNotContain(JobClasses(), t => t.Name == "PerTeamDailyJob");
    }
}
