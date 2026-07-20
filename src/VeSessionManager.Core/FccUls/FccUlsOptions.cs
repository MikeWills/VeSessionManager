namespace VeSessionManager.Core.FccUls;

public class FccUlsOptions
{
    public const string SectionName = "FccUls";

    /// <summary>Trailing-slash base for FCC's ULS download folders — "daily/a_am_mon.zip" and "complete/l_amat.zip" are appended to this. See docs/fcc-uls-watcher.md.</summary>
    public string BaseUrl { get; set; } = "https://data.fcc.gov/download/pub/uls/";
}
