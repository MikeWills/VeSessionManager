namespace VeSessionManager.Core.ExamTools;

/// <summary>
/// The result of asking ExamTools for a session's VEC archive — the file ARRL's upload page asks for
/// (issue #197).
///
/// <para>A result type rather than "bytes, or an exception", because <b>one of the two failures is
/// routine</b>: a session that has not been closed in ExamTools yet answers 403, which is not an
/// error to be logged and escalated but a state the operator can see, understand and fix themselves.
/// Anything genuinely unexpected still throws.</para>
/// </summary>
public sealed record VecArchiveDownload
{
    private VecArchiveDownload(VecArchiveDownloadOutcome outcome, byte[]? content, string? fileName, string? message)
    {
        Outcome = outcome;
        Content = content;
        FileName = fileName;
        Message = message;
    }

    public VecArchiveDownloadOutcome Outcome { get; }

    /// <summary>The archive itself. Null unless <see cref="Outcome"/> is <see cref="VecArchiveDownloadOutcome.Succeeded"/>.</summary>
    public byte[]? Content { get; }

    /// <summary>
    /// The filename ExamTools sent in <c>Content-Disposition</c> — e.g.
    /// <c>ExamSession_MARC_20260422_0130_arrl.zip</c>.
    ///
    /// <para><b>Null when the header is absent, deliberately.</b> The URL's own filename is the
    /// generic <c>ExamSession_{vec}_archive.zip</c>, identical for every session of every team, so
    /// there is nothing here to fall back to — and a plausible-looking wrong filename is worse than
    /// no filename when it is about to be filed with a VEC. The caller supplies the fallback via
    /// <see cref="VecArchiveFileName"/>, which is the only code that knows the team and the session
    /// start.</para>
    /// </summary>
    public string? FileName { get; }

    /// <summary>ExamTools' own wording for a non-success outcome, to be shown rather than paraphrased.</summary>
    public string? Message { get; }

    public static VecArchiveDownload Succeeded(byte[] content, string? fileName) =>
        new(VecArchiveDownloadOutcome.Succeeded, content, fileName, null);

    public static VecArchiveDownload SessionNotComplete(string message) =>
        new(VecArchiveDownloadOutcome.SessionNotComplete, null, null, message);
}

public enum VecArchiveDownloadOutcome
{
    Succeeded = 0,

    /// <summary>ExamTools has not marked the session complete, so there is no archive to produce yet. Self-correcting: closing the session in ExamTools is all it takes.</summary>
    SessionNotComplete = 1
}
