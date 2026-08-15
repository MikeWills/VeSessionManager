using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// A user-facing message for a session or candidate action must be written in exactly one place
/// (issue #304).
///
/// <para><b>Why a source scan rather than a behavioral test.</b> Two page models producing the same
/// message is not a defect — it is the <i>normal</i> state right up until someone fixes one of them.
/// The defect only exists in the gap between the two edits, and no test of either copy in isolation
/// can see it. What can be checked is the property that makes the gap impossible: one string, one
/// home.</para>
///
/// <para><b>Both of these already happened, in these exact files:</b></para>
/// <list type="bullet">
///   <item><b>#244</b> — <c>MarkSubmittedAsync</c> returns three values. <c>Detail</c> handles all
///   three; the copy on <c>Index</c> was never updated, so a <c>SessionNotFound</c> told the user the
///   session was "already marked submitted" — the opposite of what happened.</item>
///   <item><b>#274</b> — <c>CanSendYouthProgram</c> gated on the VEC's youth-program flag in
///   <c>CandidateDetail</c> and on nothing at all in <c>Detail</c>, so the button rendered for VECs
///   with no youth program and returned a raw enum name when clicked.</item>
/// </list>
///
/// <para>The audit diffed nine candidate handlers across these page models and found them identical
/// down to the punctuation. That is not evidence they are fine; it is the state both bugs above
/// started from.</para>
/// </summary>
public class ActionMessageSingleSourceTests
{
    /// <summary>
    /// Distinctive enough that a match is certainly the message and not prose about it. Deliberately
    /// not every message in the app — a list that has to be maintained by hand stops being maintained.
    /// These are the ones with a known history of being copied.
    /// </summary>
    private static readonly string[] Messages =
    [
        "Session marked submitted to VEC.",
        "Session is already marked submitted.",
        "Could not mark session completed.",
        "Reschedule flag cleared.",
        "Could not clear reschedule flag.",
        "Confirmation email resent.",
        "Candidate marked failed.",
        "Could not mark candidate failed.",
        "Candidate marked as withdrew/no-show; PII cleared.",
        "FRN updated.",
        "Could not update FRN.",
        "FRN cannot be blank.",
        "Payment marked paid.",
        "Could not mark payment paid.",
        "Refund requested flagged.",
        "Could not flag refund requested.",
        "Retest payment created.",
        "Felony disclosure instructions sent.",
        "Youth program instructions sent."
    ];

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(FormBindingTests.RepositoryRootPath(), "src", "VeSessionManager.Web"),
            "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void NoActionMessageIsWrittenInMoreThanOnePlace()
    {
        var files = SourceFiles().ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();

        foreach (var message in Messages)
        {
            var homes = files
                .Select(f => (File: Path.GetFileName(f), Count: Occurrences(File.ReadAllText(f), message)))
                .Where(x => x.Count > 0)
                .ToList();

            var total = homes.Sum(x => x.Count);
            if (total > 1)
            {
                offenders.Add($"\"{message}\" appears {total}× in {string.Join(", ", homes.Select(h => $"{h.File}×{h.Count}"))}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A message with two homes is one edit away from two behaviors — #244 and #274 both " +
            "started here. Move it to the shared outcome table:\n  " + string.Join("\n  ", offenders));
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The display half of the same rule (#274). Every "is this action applicable to this candidate"
    /// clause opens by excluding withdrawn candidates, and that phrase is the fingerprint of one being
    /// computed inline instead of coming from <c>CandidateCapabilities</c>.
    ///
    /// <para>One is expected and named below. The point is that a tenth capability cannot quietly
    /// appear next to it — which is how <c>CanSendYouthProgram</c> came to exist in two versions.</para>
    /// </summary>
    [Fact]
    public void CapabilityRulesAreNotComputedInsidePageModels()
    {
        var inline = SourceFiles()
            .Where(f => Path.GetFileName(f) != "CandidateCapabilities.cs")
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line.Trim()))
                .Where(x => x.Text.Contains("isWithdrawn &&", StringComparison.Ordinal)
                         || x.Text.Contains("IsWithdrawn &&", StringComparison.Ordinal)))
            .ToList();

        // CanMarkPaid, and only CanMarkPaid: the roster acts on the row's single primary payment
        // while the detail page offers the action per payment, so the two genuinely differ.
        Assert.Single(inline);
        Assert.Contains("primaryPayment", inline[0].Text);
    }

    /// <summary>
    /// The scan is only worth anything if it is looking at the real files and the strings are spelled
    /// the way the app spells them. A typo in the list above would make this test pass by finding
    /// nothing at all.
    /// </summary>
    [Fact]
    public void EveryMessageInTheListActuallyExistsSomewhereInTheApp()
    {
        var all = string.Join("\n", SourceFiles().Select(File.ReadAllText));

        var missing = Messages.Where(m => !all.Contains(m, StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0,
            "These are in the list but nowhere in the app — either renamed, or mistyped here, and " +
            "either way this test is silently checking nothing:\n  " + string.Join("\n  ", missing));
    }
}
