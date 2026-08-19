namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Where ARRL's session upload form lives (issue #197).
///
/// <para><b>Blank in the shipped <c>appsettings.json</c>, on purpose.</b> Every other integration in
/// this app can be exercised safely — Square has a sandbox, ExamTools has a dev site, email has a
/// test-mode redirect. ARRL has nothing of the kind: there is no staging endpoint and no dry-run, so
/// the first exercise of the real path files a real session with the organization that issues
/// licenses. Leaving the URL empty everywhere but production means a fresh clone, a developer machine
/// and the test suite have nowhere to post, rather than relying on nobody clicking the wrong
/// button.</para>
///
/// <para>Not a secret, so the real value lives in <c>appsettings.Production.json</c> and deploys like
/// any other setting. <c>ArrlEndpointIsNotHardcodedTests</c> fails the build if it appears
/// anywhere else.</para>
///
/// <para><b>Unconfigured refuses loudly rather than skipping quietly</b> — the one place this app
/// departs from the optional-integration pattern. A quiet skip is right for a background job that
/// will retry next poll; here it would leave somebody believing they had filed a session when
/// nothing was sent, and they would find out from the VEC.</para>
/// </summary>
public class ArrlSubmissionOptions
{
    public const string SectionName = "ArrlSubmission";

    /// <summary>
    /// The form's processing URL, including its query flag —
    /// <c>…/vec-upload.php?processed=1</c>. The bare page URL renders the form and processes nothing,
    /// most likely answering 200 with the empty form, which reads as success to anything checking
    /// status codes.
    /// </summary>
    public string? UploadUrl { get; set; }

    /// <summary>
    /// Where the filed archives and receipts are kept.
    ///
    /// <para>⚠️ <b>Must be outside the app directory.</b> <c>deploy.yml</c> runs <c>rsync --delete</c>
    /// over it on every release, which is exactly why the database lives in
    /// <c>/var/lib/vesessionmanager/</c> — anything under the app path is destroyed by the next
    /// deploy. And it needs adding to the off-box backup (#256), which today covers the database and
    /// key ring only.</para>
    /// </summary>
    public string? ArchiveRootPath { get; set; }

    /// <summary>"An operator deliberately set this", never "a shipped default happens to be non-empty".</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(UploadUrl);
}
