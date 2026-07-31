namespace VeSessionManager.Core.Uls;

public class UlsLookupOptions
{
    public const string SectionName = "UlsLookup";

    /// <summary>
    /// Trailing-slash base for ExamTools' ULS mirror — "api/uls/lookup2/{frn}" is appended.
    /// Deliberately global rather than per-Team (unlike every other ExamTools call): this endpoint
    /// is unauthenticated and returns public FCC data, so it has no per-team credential or team
    /// scoping. See docs/uls-watcher.md.
    /// </summary>
    public string BaseUrl { get; set; } = "https://exam.tools/";
}
